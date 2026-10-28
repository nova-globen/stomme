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
    /// BL-229 - Jira DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for a Jira site: a Jira connection made over the API, its API token vaulted, tested (the
    /// account named); a published pipeline creates an issue, comments on it and moves it to Done, then finds the open ones -
    /// each call with the account's email and token by the Basic scheme; the token is in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class JiraDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_opens_an_issue_comments_moves_it_to_done_and_finds_the_open_ones()
        {
            var token = "jira-" + Guid.NewGuid().ToString("N");
            using var jira = new JiraStandIn();
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
                    Name = "Tracker", ConnectorType = "jira", Endpoint = $"http://127.0.0.1:{jira.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["username"] = "ops@acme.example", ["password"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Tracker" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-open", Name = "open" } },
                    Steps =
                    {
                        new PipelineGraphStep { Id = "create", Type = "Stomme.ExtractFromConnection", Name = "Open a bug", Inputs = { ["Connection"] = connection, ["Operation"] = "create", ["Parameters"] = "project = OPS\nissue_type = Bug\nsummary = Order 1001 stuck\nlabels = billing" } },
                        new PipelineGraphStep { Id = "comment", Type = "Stomme.ExtractFromConnection", Name = "Say so", Inputs = { ["Connection"] = connection, ["Operation"] = "comment", ["Parameters"] = "issue = OPS-42\ntext = Shipped today." } },
                        new PipelineGraphStep { Id = "move", Type = "Stomme.ExtractFromConnection", Name = "Close it", Inputs = { ["Connection"] = connection, ["Operation"] = "transition", ["Parameters"] = "issue = OPS-42\nto = Done" } },
                        new PipelineGraphStep { Id = "search", Type = "Stomme.ExtractFromConnection", Name = "Open issues", Inputs = { ["Connection"] = connection, ["Operation"] = "search", ["Parameters"] = "jql = project = OPS AND status != Done" }, OutputVariables = { ["Result"] = "v-open" } },
                    },
                    Connections =
                    {
                        new PipelineGraphConnection { SourceId = "create", SourcePort = "Done", TargetId = "comment" },
                        new PipelineGraphConnection { SourceId = "comment", SourcePort = "Done", TargetId = "move" },
                        new PipelineGraphConnection { SourceId = "move", SourcePort = "Done", TargetId = "search" },
                    },
                };

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
                var received = jira.Received;
                var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ops@acme.example:" + token));
                Assert.All(received, request => Assert.Equal(basic, request.Authorization));
                Assert.Equal("""{"fields":{"summary":"Order 1001 stuck","labels":["billing"],"project":{"key":"OPS"},"issuetype":{"name":"Bug"}}}""", received.Single(request => request.Path == "/rest/api/2/issue").Body);
                Assert.Equal("""{"body":"Shipped today."}""", received.Single(request => request.Path == "/rest/api/2/issue/OPS-42/comment").Body);
                Assert.Equal("""{"transition":{"id":"31"}}""", received.Single(request => request.Method == "POST" && request.Path == "/rest/api/2/issue/OPS-42/transitions").Body);
                Assert.Equal("project = OPS AND status != Done", JsonNode.Parse(received.Single(request => request.Path == "/rest/api/3/search/jql").Body)!["jql"]!.GetValue<string>());

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.Contains("OPS-7", steps); // the open issues found were the search's result
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a Jira site: an account, an issue created as OPS-42, its comment, its transitions, one open issue found.</summary>
        private sealed class JiraStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string Path, string? Authorization, string Body)> _received = new();

            public JiraStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Method, string Path, string? Authorization, string Body)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

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
                        _received.Add((method, path, context.Request.Headers["Authorization"], body));
                    }

                    var (status, answer) = (method, path) switch
                    {
                        ("GET", "/rest/api/3/myself") => (200, """{"accountId":"a1","displayName":"Ops Bot"}"""),
                        ("POST", "/rest/api/2/issue") => (201, """{"id":"10042","key":"OPS-42","self":"x"}"""),
                        ("POST", "/rest/api/2/issue/OPS-42/comment") => (201, """{"id":"777"}"""),
                        ("GET", "/rest/api/2/issue/OPS-42/transitions") => (200, """{"transitions":[{"id":"11","name":"Start","to":{"name":"In Progress"}},{"id":"31","name":"Finish","to":{"name":"Done"}}]}"""),
                        ("POST", "/rest/api/2/issue/OPS-42/transitions") => (204, ""),
                        ("POST", "/rest/api/3/search/jql") => (200, """{"issues":[{"id":"10007","key":"OPS-7","fields":{"summary":"Order 1007","status":{"name":"To Do"}}}],"isLast":true}"""),
                        _ => (404, """{"errorMessages":["Not found."]}"""),
                    };
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = status;
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
