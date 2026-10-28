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
    /// BL-229 - Linear DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Linear's GraphQL API: a Linear connection made over the API, its key vaulted, tested (the
    /// user named); a published pipeline creates an issue by its team's key and a state's name - each looked up - reads the
    /// team's open issues over two pages by cursor, comments on one and links an order to it - every call the key in Linear's own
    /// header; the key is in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class LinearDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string TeamId = "11111111-2222-3333-4444-555555555555";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_reads_comments_and_links()
        {
            var key = "lin_api_" + Guid.NewGuid().ToString("N");
            using var linear = new LinearStandIn();
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
                    Name = "Engineering", ConnectorType = "linear", Endpoint = $"http://127.0.0.1:{linear.Port}", TimeoutSeconds = 10, Credentials = new() { ["api-key"] = key },
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
                        Call("file", "File the bug", "create_issue", "team = eng\ntitle = Printer on fire\nstate = Todo\npriority = 1"),
                        Call("open", "Open issues", "issues", "team = ENG\nstate = Todo\nmax = 150"),
                        Call("comment", "Comment", "add_comment", "issue = ENG-7\nbody = Looking."),
                        Call("link", "Link the order", "add_link", "issue = ENG-7\nurl = https://shop.example.com/orders/1001\ntitle = Order 1001"),
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
                var received = linear.Received;
                Assert.All(received, request => Assert.Equal(("/graphql", key), (request.Path, request.Authorization))); // a personal key as Linear takes it
                Assert.Equal("ENG", received.Single(request => request.Query.Contains("teams(filter", StringComparison.Ordinal)).Variables["key"]!.GetValue<string>());
                Assert.Equal((TeamId, "Todo"), received.Where(request => request.Query.Contains("workflowStates", StringComparison.Ordinal)).Select(request => (request.Variables["team"]!.GetValue<string>(), request.Variables["name"]!.GetValue<string>())).Single());
                Assert.Equal("{\"title\":\"Printer on fire\",\"priority\":1,\"teamId\":\"" + TeamId + "\",\"stateId\":\"s-todo\"}", received.Single(request => request.Query.Contains("issueCreate", StringComparison.Ordinal)).Variables["input"]!.ToJsonString());
                var pages = received.Where(request => request.Query.Contains("issues(first", StringComparison.Ordinal)).ToList();
                Assert.Equal(2, pages.Count);
                Assert.Equal(("c1", 50), (pages[1].Variables["after"]!.GetValue<string>(), pages[1].Variables["first"]!.GetValue<int>()));
                Assert.Equal("{\"team\":{\"key\":{\"eq\":\"ENG\"}},\"state\":{\"name\":{\"eq\":\"Todo\"}}}", pages[0].Variables["filter"]!.ToJsonString());
                Assert.Equal("{\"input\":{\"issueId\":\"ENG-7\",\"body\":\"Looking.\"}}", received.Single(request => request.Query.Contains("commentCreate", StringComparison.Ordinal)).Variables.ToJsonString());
                Assert.Equal("{\"issueId\":\"ENG-7\",\"url\":\"https://shop.example.com/orders/1001\",\"title\":\"Order 1001\"}", received.Single(request => request.Query.Contains("attachmentLinkURL", StringComparison.Ordinal)).Variables.ToJsonString());

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var open = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "open")!;
                Assert.Contains("\"RecordCount\": 150", open["outputsJson"]!.GetValue<string>()); // both pages, up to the most asked for
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Linear's GraphQL API: the user, a team and a state looked up, an issue made, issues over two pages, a comment, a link.</summary>
        private sealed class LinearStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Path, string? Authorization, string Query, JsonObject Variables)> _received = new();

            public LinearStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Path, string? Authorization, string Query, JsonObject Variables)> Received
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
                "{\"id\":\"i" + number + "\",\"identifier\":\"ENG-" + number + "\",\"title\":\"Fix " + number + "\",\"url\":\"https://linear.app/acme/issue/ENG-" + number + "\",\"priority\":1,\"state\":{\"name\":\"Todo\"},\"team\":{\"key\":\"ENG\"},\"labels\":{\"nodes\":[]}}";

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

                    var request = JsonNode.Parse(body)!;
                    var query = request["query"]!.GetValue<string>();
                    var variables = request["variables"] as JsonObject ?? new JsonObject();
                    lock (_gate)
                    {
                        _received.Add((context.Request.Url!.AbsolutePath, context.Request.Headers["Authorization"], query, (JsonObject)variables.DeepClone()));
                    }

                    var answer = query switch
                    {
                        _ when query.Contains("viewer", StringComparison.Ordinal) => "{\"data\":{\"viewer\":{\"name\":\"Ada Lovelace\",\"email\":\"ada@example.com\"}}}",
                        _ when query.Contains("teams(filter", StringComparison.Ordinal) => "{\"data\":{\"teams\":{\"nodes\":[{\"id\":\"" + TeamId + "\"}]}}}",
                        _ when query.Contains("workflowStates", StringComparison.Ordinal) => "{\"data\":{\"workflowStates\":{\"nodes\":[{\"id\":\"s-todo\"}]}}}",
                        _ when query.Contains("issueCreate", StringComparison.Ordinal) => "{\"data\":{\"issueCreate\":{\"success\":true,\"issue\":" + Issue(7) + "}}}",
                        _ when query.Contains("issues(first", StringComparison.Ordinal) && variables["after"] is null =>
                            "{\"data\":{\"issues\":{\"nodes\":[" + string.Join(",", Enumerable.Range(1, 100).Select(Issue)) + "],\"pageInfo\":{\"hasNextPage\":true,\"endCursor\":\"c1\"}}}}",
                        _ when query.Contains("issues(first", StringComparison.Ordinal) =>
                            "{\"data\":{\"issues\":{\"nodes\":[" + string.Join(",", Enumerable.Range(101, 100).Select(Issue)) + "],\"pageInfo\":{\"hasNextPage\":false,\"endCursor\":\"c2\"}}}}",
                        _ when query.Contains("commentCreate", StringComparison.Ordinal) => "{\"data\":{\"commentCreate\":{\"success\":true,\"comment\":{\"id\":\"c1\",\"body\":\"Looking.\"}}}}",
                        _ when query.Contains("attachmentLinkURL", StringComparison.Ordinal) => "{\"data\":{\"attachmentLinkURL\":{\"success\":true,\"attachment\":{\"id\":\"a1\",\"url\":\"https://shop.example.com/orders/1001\",\"title\":\"Order 1001\"}}}}",
                        _ => "{\"errors\":[{\"message\":\"Unknown query\",\"extensions\":{\"code\":\"GRAPHQL_VALIDATION_FAILED\"}}]}",
                    };
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(bytes);
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
