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
    /// BL-229 - GitHub DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for GitHub's API: a GitHub connection made over the API, its token vaulted, tested (the account
    /// named); a published pipeline opens an issue, comments on it, closes it, commits a status file, starts a deploy workflow and
    /// reads the open issues over two pages by GitHub's Link header - each call a bearer with GitHub's headers; the token is in
    /// neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GitHubDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_opens_comments_on_and_closes_an_issue_commits_a_file_starts_a_workflow_and_reads_the_open_issues()
        {
            var token = "ghp_" + Guid.NewGuid().ToString("N");
            using var github = new GitHubStandIn();
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
                    Name = "Shop repository", ConnectorType = "github", Endpoint = $"http://127.0.0.1:{github.Port}", TimeoutSeconds = 10, Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);
                Assert.Contains(github.Received, request => request.Method == "GET" && request.Path == "/user"); // the test asked who the token is

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Shop housekeeping" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = "repository = acme/shop\n" + parameters } };
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-open", Name = "open" } },
                    Steps =
                    {
                        Call("open", "Open an issue", "create_issue", "title = Order 1001 stuck\nbody = Twelve, please.\nlabels = bug, billing"),
                        Call("comment", "Say so", "comment", "issue = 42\nbody = Shipped today."),
                        Call("close", "Close it", "update_issue", "issue = 42\nstate = closed\nstate_reason = completed"),
                        Call("status", "Write the status", "commit_file", "path = STATUS.md\ncontent = All shipped.\nmessage = Status\nbranch = main"),
                        Call("deploy", "Deploy", "dispatch", "workflow = deploy.yml\nref = main\ninputs = {\"environment\": \"uat\"}"),
                        Call("issues", "Open issues", "issues", "state = open"),
                    },
                };
                graph.Steps.Last().OutputVariables["Result"] = "v-open";
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
                var received = github.Received;
                Assert.All(received, request => Assert.Equal(("Bearer " + token, "Stomme", "2022-11-28"), (request.Authorization, request.UserAgent, request.Version)));
                Assert.Equal("""{"title":"Order 1001 stuck","body":"Twelve, please.","labels":["bug","billing"]}""", received.Single(request => request.Method == "POST" && request.Path == "/repos/acme/shop/issues").Body);
                Assert.Equal("""{"body":"Shipped today."}""", received.Single(request => request.Path == "/repos/acme/shop/issues/42/comments").Body);
                Assert.Equal("""{"state":"closed","state_reason":"completed"}""", received.Single(request => request.Method == "PATCH").Body);
                Assert.Equal("""{"message":"Status","content":"QWxsIHNoaXBwZWQu","branch":"main"}""", received.Single(request => request.Method == "PUT").Body); // none there: no sha
                Assert.Equal("""{"ref":"main","inputs":{"environment":"uat"}}""", received.Single(request => request.Path.EndsWith("/dispatches", StringComparison.Ordinal)).Body);
                Assert.Equal(2, received.Count(request => request.Method == "GET" && request.Path.EndsWith("/issues", StringComparison.Ordinal))); // both pages, by GitHub's own link

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.Contains("Order 1007", steps);
                Assert.Contains("Order 1008", steps); // the second page's issue too
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for GitHub's API: an account, an issue opened as #42, its comment, its closing, a file committed, a workflow started, the open issues over two pages.</summary>
        private sealed class GitHubStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string Path, string? Authorization, string? UserAgent, string? Version, string Body)> _received = new();

            public GitHubStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Method, string Path, string? Authorization, string? UserAgent, string? Version, string Body)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Issue(int number) =>
                $$"""{"number":{{number}},"title":"Order 100{{number}}","state":"open","html_url":"https://github.example/acme/shop/issues/{{number}}","user":{"login":"ada"},"labels":[],"assignees":[],"body":"","comments":0}""";

            private async Task ServeAsync()
            {
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

                    var path = context.Request.Url!.AbsolutePath;
                    var method = context.Request.HttpMethod;
                    lock (_gate)
                    {
                        _received.Add((method, path, context.Request.Headers["Authorization"], context.Request.Headers["User-Agent"], context.Request.Headers["X-GitHub-Api-Version"], body));
                    }

                    string? link = null;
                    var (status, answer) = (method, path) switch
                    {
                        ("GET", "/user") => (200, """{"login":"ops-bot","id":1}"""),
                        ("POST", "/repos/acme/shop/issues") => (201, """{"number":42,"html_url":"https://github.example/acme/shop/issues/42"}"""),
                        ("POST", "/repos/acme/shop/issues/42/comments") => (201, """{"id":777,"html_url":"https://github.example/acme/shop/issues/42#issuecomment-777"}"""),
                        ("PATCH", "/repos/acme/shop/issues/42") => (200, """{"number":42,"state":"closed","html_url":"https://github.example/acme/shop/issues/42"}"""),
                        ("GET", "/repos/acme/shop/contents/STATUS.md") => (404, """{"message":"Not Found"}"""),
                        ("PUT", "/repos/acme/shop/contents/STATUS.md") => (201, """{"content":{"path":"STATUS.md","sha":"s1"},"commit":{"sha":"c0ffee","html_url":"https://github.example/acme/shop/commit/c0ffee"}}"""),
                        ("POST", "/repos/acme/shop/actions/workflows/deploy.yml/dispatches") => (204, ""),
                        ("GET", "/repos/acme/shop/issues") => (200, "[" + Issue(7) + "]"),
                        ("GET", "/repositories/1/issues") => (200, "[" + Issue(8) + "]"),
                        _ => (404, """{"message":"Not Found"}"""),
                    };
                    if (path == "/repos/acme/shop/issues" && method == "GET")
                    {
                        link = $"<http://127.0.0.1:{Port}/repositories/1/issues?state=open&per_page=100&page=2>; rel=\"next\"";
                    }

                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = status;
                    if (link is not null)
                    {
                        context.Response.AddHeader("Link", link);
                    }

                    if (bytes.Length > 0)
                    {
                        context.Response.ContentType = "application/json";
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
