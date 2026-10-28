#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Models.Pipelines;
using Stomme.Tests.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - GitLab DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for GitLab's REST API: a GitLab connection made over the API, its token vaulted, tested (the
    /// user named); a published pipeline opens an issue assigned by username (looked up), reads the project's open issues over two
    /// pages, comments on one, commits a file that is not there yet and runs a pipeline - every call the token in GitLab's
    /// PRIVATE-TOKEN header; the token is in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GitLabDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_opens_reads_comments_commits_and_runs()
        {
            var token = "glpat-" + Guid.NewGuid().ToString("N");
            using var gitlab = new GitLabStandIn();
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Pipelines"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                Task<(HttpStatusCode Status, string Body)> Send(HttpMethod method, string path, object body, string label) =>
                    GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, method, path, body, output, label);

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = "127.0.0.1" }, "the Host allows loopback")).Status);
                var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                {
                    Name = "Shop code", ConnectorType = "gitlab", Endpoint = $"http://127.0.0.1:{gitlab.Port}/acme/shop/-/issues", TimeoutSeconds = 10, Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Triage" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("open", "Open the bug", "create_issue", "project = acme/shop\ntitle = Printer on fire\nlabels = bug\nassignees = grace"),
                        Call("read", "Open issues", "issues", "project = acme/shop\nlabels = bug\nmax = 150"),
                        Call("note", "Comment", "comment", "project = acme/shop\nissue = 12\nbody = Looking."),
                        Call("commit", "Write the notes", "commit_file", "project = acme/shop\npath = docs/NOTES.md\nbranch = main\ncontent = Printer fixed.\nmessage = Notes"),
                        Call("deploy", "Deploy", "pipeline", "project = acme/shop\nref = main\nvariables = {\"DEPLOY\": \"true\"}"),
                    },
                };
                for (var i = 1; i < graph.Steps.Count; i++)
                {
                    graph.Connections.Add(new PipelineGraphConnection { SourceId = graph.Steps[i - 1].Id, SourcePort = "Done", TargetId = graph.Steps[i].Id });
                }

                var saved = await Send(HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, "save the graph");
                Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);
                var published = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish");
                Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);
                var started = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest(), "run");
                var runId = JsonNode.Parse(started.Body)!["runId"]!.GetValue<string>();
                PipelineRunView? run = null;
                for (var attempt = 0; attempt < 150 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                Assert.Equal("Finished", run?.SubStatus);
                var received = gitlab.Received;
                Assert.All(received, request => Assert.Equal(token, request.Token));
                Assert.Equal("/api/v4/user", received[0].PathAndQuery); // the test, a page's address cut back to the instance
                const string project = "/api/v4/projects/acme%2Fshop";
                Assert.Equal(("GET", "/api/v4/users?username=grace"), received.Where(request => request.PathAndQuery.StartsWith("/api/v4/users?", StringComparison.Ordinal)).Select(request => (request.Method, request.PathAndQuery)).Single());
                Assert.Equal("{\"title\":\"Printer on fire\",\"labels\":\"bug\",\"assignee_ids\":[21]}", received.Single(request => request.Method == "POST" && request.PathAndQuery == project + "/issues").Body);
                Assert.Equal(new[] { project + "/issues?state=opened&labels=bug&order_by=updated_at&sort=desc&per_page=100&page=1", project + "/issues?state=opened&labels=bug&order_by=updated_at&sort=desc&per_page=50&page=2" },
                    received.Where(request => request.Method == "GET" && request.PathAndQuery.StartsWith(project + "/issues?", StringComparison.Ordinal)).Select(request => request.PathAndQuery));
                Assert.Equal("{\"body\":\"Looking.\"}", received.Single(request => request.PathAndQuery == project + "/issues/12/notes").Body);
                Assert.Equal(new[] { ("HEAD", project + "/repository/files/docs%2FNOTES.md?ref=main"), ("POST", project + "/repository/files/docs%2FNOTES.md") },
                    received.Where(request => request.PathAndQuery.Contains("/repository/files/", StringComparison.Ordinal)).Select(request => (request.Method, request.PathAndQuery)));
                Assert.Equal("{\"ref\":\"main\",\"variables\":[{\"key\":\"DEPLOY\",\"value\":\"true\"}]}", received.Single(request => request.PathAndQuery == project + "/pipeline").Body);

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var read2 = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "read")!;
                Assert.Contains("\"RecordCount\": 150", read2["outputsJson"]!.GetValue<string>()); // both pages, up to the most asked for
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for GitLab's REST API: the user, a user looked up, an issue opened, the issues over pages, a note, a file written, a pipeline.</summary>
        private sealed class GitLabStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string PathAndQuery, string? Token, string Body)> _received = new();

            public GitLabStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Method, string PathAndQuery, string? Token, string Body)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Issues(int from, int count) =>
                "[" + string.Join(",", Enumerable.Range(from, count).Select(iid => "{\"id\":" + (9000 + iid) + ",\"iid\":" + iid + ",\"title\":\"Fix " + iid + "\",\"state\":\"opened\",\"labels\":[\"bug\"]}")) + "]";

            private async Task ServeAsync()
            {
                const string project = "/api/v4/projects/acme%2Fshop";
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                    {
                        return;
                    }

                    string body;
                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    {
                        body = await reader.ReadToEndAsync();
                    }

                    var raw = context.Request.RawUrl ?? "/";
                    var path = raw.Split('?')[0];
                    var method = context.Request.HttpMethod;
                    lock (_gate)
                    {
                        _received.Add((method, raw, context.Request.Headers["PRIVATE-TOKEN"], body));
                    }

                    string? next = null;
                    var (status, answer) = (method, path) switch
                    {
                        ("GET", "/api/v4/user") => (200, "{\"id\":7,\"username\":\"ada\",\"name\":\"Ada Lovelace\"}"),
                        ("GET", "/api/v4/users") => (200, "[{\"id\":21,\"username\":\"grace\"}]"),
                        ("POST", project + "/issues") => (201, "{\"id\":9012,\"iid\":12,\"title\":\"Printer on fire\",\"state\":\"opened\"}"),
                        ("GET", project + "/issues") when raw.EndsWith("page=1", StringComparison.Ordinal) => (200, Issues(1, 100)),
                        ("GET", project + "/issues") => (200, Issues(101, 100)),
                        ("POST", project + "/issues/12/notes") => (201, "{\"id\":501,\"body\":\"Looking.\"}"),
                        ("HEAD", project + "/repository/files/docs%2FNOTES.md") => (404, ""),
                        ("POST", project + "/repository/files/docs%2FNOTES.md") => (201, "{\"file_path\":\"docs/NOTES.md\",\"branch\":\"main\"}"),
                        ("POST", project + "/pipeline") => (201, "{\"id\":777,\"status\":\"created\",\"ref\":\"main\"}"),
                        _ => (404, "{\"message\":\"404 Not found\"}"),
                    };
                    if (method == "GET" && path == project + "/issues" && raw.EndsWith("page=1", StringComparison.Ordinal))
                    {
                        next = "2";
                    }

                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    context.Response.Headers["X-Next-Page"] = next ?? "";
                    if (method != "HEAD" && answer.Length > 0)
                    {
                        var bytes = Encoding.UTF8.GetBytes(answer);
                        await context.Response.OutputStream.WriteAsync(bytes);
                    }

                    context.Response.Close();
                }
            }

            public void Dispose()
            {
                _listener.Stop();
                _listener.Close();
            }
        }
    }
}
