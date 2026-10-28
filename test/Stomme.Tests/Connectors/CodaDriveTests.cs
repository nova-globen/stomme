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
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Models.Pipelines;
using Stomme.Tests.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Coda DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Coda's API v1 that refuses a call without the token: a Coda connection made over the API,
    /// its token vaulted, tested; a published pipeline creates two rows, one with an accent, waiting until Coda has applied
    /// them, reads a table's rows by name over two pages - no more than asked - updates a row, pushes its button and deletes the
    /// rows; the token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class CodaDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Doc = "AbCDeFGH";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_reads_updates_pushes_and_deletes_rows_with_its_token()
        {
            var token = "ct" + Guid.NewGuid().ToString("N");
            using var coda = new CodaStandIn(token);
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
                    Name = "Shop docs", ConnectorType = "coda", Endpoint = $"http://127.0.0.1:{coda.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["token"] = token },
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
                        Call("create", "New orders", "create_rows", $"doc = {Doc}\ntable = Orders\nfields = [{{\"Customer\": \"Café Ada\", \"Total\": 99.5}}, {{\"Customer\": \"Bo\", \"Total\": 12}}]\nwait = true"),
                        Call("read", "Paid orders", "rows", $"doc = {Doc}\ntable = Orders\nquery = Paid:true\nsort_by = natural\nmax = 120"),
                        Call("update", "Paid", "update_row", $"doc = {Doc}\ntable = Orders\nrow = i-43\nfields = {{\"Paid\": true}}"),
                        Call("push", "Ship", "push_button", $"doc = {Doc}\ntable = Orders\nrow = i-43\ncolumn = Ship it\nwait = true"),
                        Call("delete", "Drop", "delete_rows", $"doc = {Doc}\ntable = Orders\nrows = i-43, i-44"),
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
                var received = coda.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token: " + request.Line));
                var rows = $"/apis/v1/docs/{Doc}/tables/Orders/rows";
                Assert.Equal(new[]
                {
                    "GET /apis/v1/whoami",
                    $"POST {rows} {{\"rows\":[{{\"cells\":[{{\"column\":\"Customer\",\"value\":\"Café Ada\"}},{{\"column\":\"Total\",\"value\":99.5}}]}},{{\"cells\":[{{\"column\":\"Customer\",\"value\":\"Bo\"}},{{\"column\":\"Total\",\"value\":12}}]}}]}}",
                    "GET /apis/v1/mutationStatus/mutate%3A1",
                    $"GET {rows}?useColumnNames=true&query=Paid%3Atrue&sortBy=natural&limit=100",
                    $"GET {rows}?useColumnNames=true&query=Paid%3Atrue&sortBy=natural&limit=20&pageToken=page2",
                    $"PUT {rows}/i-43 {{\"row\":{{\"cells\":[{{\"column\":\"Paid\",\"value\":true}}]}}}}",
                    $"POST {rows}/i-43/buttons/Ship%20it",
                    "GET /apis/v1/mutationStatus/mutate%3A3",
                    $"DELETE {rows} {{\"rowIds\":[\"i-43\",\"i-44\"]}}",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("i-43", JsonNode.Parse(Outputs("create"))!["Result"]![0]!["id"]!.GetValue<string>()); // the ids Coda added
                Assert.Equal(120, JsonNode.Parse(Outputs("read"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal(2, JsonNode.Parse(Outputs("delete"))!["RecordCount"]!.GetValue<int>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Coda's API v1 that answers only a call carrying the token.</summary>
        private sealed class CodaStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _token;
            private readonly List<(string Line, bool Authorized)> _received = new();
            private int _mutations;

            public CodaStandIn(string token)
            {
                _token = token;
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Line, bool Authorized)> Received
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

                    var raw = context.Request.RawUrl ?? "";
                    var authorized = context.Request.Headers["Authorization"] == "Bearer " + _token;
                    lock (_gate)
                    {
                        _received.Add(($"{context.Request.HttpMethod} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    string Rows(int from, int count) => "[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"id\":\"i-" + i + "\",\"type\":\"row\",\"name\":\"Café " + i + "\",\"index\":" + i + ",\"values\":{\"Customer\":\"Café " + i + "\",\"Total\":" + (1000 - i) + ",\"Paid\":true}}")) + "]";
                    string Mutation(string more) => "{\"requestId\":\"mutate:" + Interlocked.Increment(ref _mutations) + "\"" + more + "}";
                    var (status, answer) = !authorized ? (401, "{\"statusCode\":401,\"statusMessage\":\"Unauthorized\",\"message\":\"The API token is invalid.\"}")
                        : (context.Request.HttpMethod, raw) switch
                        {
                            ("GET", "/apis/v1/whoami") => (200, "{\"name\":\"Shop\",\"type\":\"user\",\"scoped\":false,\"tokenName\":\"Pipelines\"}"),
                            ("GET", _) when raw.StartsWith("/apis/v1/mutationStatus/", StringComparison.Ordinal) => (200, "{\"completed\":true}"),
                            ("GET", _) when raw.Contains("pageToken=page2", StringComparison.Ordinal) => (200, "{\"items\":" + Rows(101, 50) + "}"),
                            ("GET", _) when raw.Contains("/rows?", StringComparison.Ordinal) => (200, "{\"items\":" + Rows(1, 100) + ",\"nextPageToken\":\"page2\"}"),
                            ("POST", _) when raw.Contains("/buttons/", StringComparison.Ordinal) => (202, Mutation(",\"rowId\":\"i-43\",\"columnId\":\"c-Ship\"")),
                            ("POST", _) => (202, Mutation(",\"addedRowIds\":[\"i-43\",\"i-44\"]")),
                            ("PUT", _) => (202, Mutation(",\"id\":\"i-43\"")),
                            ("DELETE", _) => (202, Mutation(",\"rowIds\":[\"i-43\",\"i-44\"]")),
                            _ => (404, "{\"statusCode\":404,\"statusMessage\":\"Not Found\"}"),
                        };
                    context.Response.StatusCode = status;
                    var bytes = Encoding.UTF8.GetBytes(answer);
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
