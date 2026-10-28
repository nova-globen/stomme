#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
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
    /// BL-229 - monday.com DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for monday's GraphQL API: a monday.com connection made over the API, its token vaulted, tested (the
    /// user named); a published pipeline creates an item with column values, reads a board's items over two pages by cursor, changes
    /// one's status, moves it and posts an update - every call the token in Authorization alone; the token is in neither the graph
    /// nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class MondayDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        [Fact]
        public async Task A_pipeline_creates_reads_changes_moves_and_updates()
        {
            var token = "eyJ" + Guid.NewGuid().ToString("N");
            using var monday = new MondayStandIn();
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
                    Name = "Orders board", ConnectorType = "monday", Endpoint = $"http://127.0.0.1:{monday.Port}", TimeoutSeconds = 10, Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Orders" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("add", "Add the order", "create_item", "board = 1234567890\ngroup = topics\nname = Order 1001\ncolumns = {\"status\": {\"label\": \"Working on it\"}}"),
                        Call("read", "The board", "items", "board = 1234567890\nmax = 150"),
                        Call("change", "Done", "update_item", "board = 1234567890\nitem = 9\ncolumns = {\"status\": {\"label\": \"Done\"}}"),
                        Call("move", "Move it", "move_item", "item = 9\ngroup = done_group"),
                        Call("post", "Post", "add_update", "item = 9\nbody = Shipped."),
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
                var received = monday.Received;
                Assert.All(received, request => Assert.Equal(("/v2", token), (request.Path, request.Authorization)));
                Assert.Equal("{\"board\":\"1234567890\",\"group\":\"topics\",\"name\":\"Order 1001\",\"columns\":\"{\\\"status\\\":{\\\"label\\\":\\\"Working on it\\\"}}\"}", received.Single(request => request.Query.Contains("create_item", StringComparison.Ordinal)).Variables.ToJsonString(Wire));
                Assert.Equal(new[] { "{\"board\":\"1234567890\",\"limit\":100}", "{\"cursor\":\"c1\",\"limit\":50}" },
                    received.Where(request => request.Query.StartsWith("query First", StringComparison.Ordinal) || request.Query.StartsWith("query Next", StringComparison.Ordinal)).Select(request => request.Variables.ToJsonString(Wire)));
                Assert.Equal("{\"board\":\"1234567890\",\"item\":\"9\",\"columns\":\"{\\\"status\\\":{\\\"label\\\":\\\"Done\\\"}}\"}", received.Single(request => request.Query.Contains("change_multiple_column_values", StringComparison.Ordinal)).Variables.ToJsonString(Wire));
                Assert.Equal("{\"item\":\"9\",\"group\":\"done_group\"}", received.Single(request => request.Query.Contains("move_item_to_group", StringComparison.Ordinal)).Variables.ToJsonString(Wire));
                Assert.Equal("{\"item\":\"9\",\"body\":\"Shipped.\"}", received.Single(request => request.Query.Contains("create_update", StringComparison.Ordinal)).Variables.ToJsonString(Wire));

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

        /// <summary>A loopback stand-in for monday's GraphQL API: the user, an item made, a board's items over two pages, one changed, moved and updated.</summary>
        private sealed class MondayStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Path, string? Authorization, string Query, JsonObject Variables)> _received = new();

            public MondayStandIn()
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

            private static string Item(int id) => "{\"id\":\"" + id + "\",\"name\":\"Order " + id + "\",\"state\":\"active\",\"board\":{\"id\":\"1234567890\"},\"group\":{\"id\":\"topics\",\"title\":\"To do\"},\"column_values\":[{\"id\":\"status\",\"text\":\"Working on it\"}]}";

            private static string Items(int from, int count) => string.Join(",", Enumerable.Range(from, count).Select(Item));

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
                        _ when query.Contains("me {", StringComparison.Ordinal) => "{\"data\":{\"me\":{\"name\":\"Ada Lovelace\",\"email\":\"ada@example.com\",\"account\":{\"name\":\"Acme\"}}}}",
                        _ when query.Contains("create_item", StringComparison.Ordinal) => "{\"data\":{\"create_item\":" + Item(9) + "}}",
                        _ when query.StartsWith("query First", StringComparison.Ordinal) => "{\"data\":{\"boards\":[{\"items_page\":{\"cursor\":\"c1\",\"items\":[" + Items(1, 100) + "]}}]}}",
                        _ when query.StartsWith("query Next", StringComparison.Ordinal) => "{\"data\":{\"next_items_page\":{\"cursor\":\"c2\",\"items\":[" + Items(101, 100) + "]}}}",
                        _ when query.Contains("change_multiple_column_values", StringComparison.Ordinal) => "{\"data\":{\"change_multiple_column_values\":" + Item(9) + "}}",
                        _ when query.Contains("move_item_to_group", StringComparison.Ordinal) => "{\"data\":{\"move_item_to_group\":" + Item(9) + "}}",
                        _ when query.Contains("create_update", StringComparison.Ordinal) => "{\"data\":{\"create_update\":{\"id\":\"501\",\"body\":\"Shipped.\"}}}",
                        _ => "{\"errors\":[{\"message\":\"Unknown query\"}]}",
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
