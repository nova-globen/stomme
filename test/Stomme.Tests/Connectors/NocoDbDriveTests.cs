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
    /// NocoDB DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for NocoDB's v2 data API that refuses a call without the token: a NocoDB connection made over
    /// the API, its token vaulted, tested; a published pipeline creates a record with an accent, reads a table's paid records
    /// over two pages - no more than asked - updates the record, counts the table and deletes the record; the token in neither
    /// the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class NocoDbDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Table = "md_orders";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_reads_updates_counts_and_deletes_with_its_token()
        {
            var token = "nc" + Guid.NewGuid().ToString("N");
            using var nocodb = new NocoDbStandIn(token);
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
                    Name = "Shop tables", ConnectorType = "nocodb", Endpoint = $"http://127.0.0.1:{nocodb.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = token },
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
                        Call("create", "New order", "create_record", $"table = {Table}\nfields = {{\"Customer\": \"Café Ada\", \"Total\": 99.5, \"Paid\": false}}"),
                        Call("read", "Paid orders", "records", $"table = {Table}\nwhere = (Paid,eq,true)\nsort = -Total\nmax = 120"),
                        Call("update", "Paid", "update_record", $"table = {Table}\nid = 43\nfields = {{\"Paid\": true}}"),
                        Call("count", "How many", "count_records", $"table = {Table}\nwhere = (Paid,eq,true)"),
                        Call("delete", "Drop", "delete_record", $"table = {Table}\nid = 43"),
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
                var received = nocodb.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /api/v2/meta/bases/",
                    $"POST /api/v2/tables/{Table}/records {{\"Customer\":\"Café Ada\",\"Total\":99.5,\"Paid\":false}}",
                    $"GET /api/v2/tables/{Table}/records?where=%28Paid%2Ceq%2Ctrue%29&sort=-Total&offset=0&limit=100",
                    $"GET /api/v2/tables/{Table}/records?where=%28Paid%2Ceq%2Ctrue%29&sort=-Total&offset=100&limit=20",
                    $"PATCH /api/v2/tables/{Table}/records {{\"Paid\":true,\"Id\":43}}",
                    $"GET /api/v2/tables/{Table}/records/count?where=%28Paid%2Ceq%2Ctrue%29",
                    $"DELETE /api/v2/tables/{Table}/records {{\"Id\":43}}",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(43, JsonNode.Parse(Outputs("create"))!["Result"]![0]!["Id"]!.GetValue<int>()); // its key as NocoDB gave it
                Assert.Equal(120, JsonNode.Parse(Outputs("read"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal(150, JsonNode.Parse(Outputs("count"))!["Result"]![0]!["count"]!.GetValue<int>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for NocoDB's v2 data API that answers only a call carrying the token.</summary>
        private sealed class NocoDbStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _token;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public NocoDbStandIn(string token)
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
                    var authorized = context.Request.Headers["xc-token"] == _token;
                    lock (_gate)
                    {
                        _received.Add(($"{context.Request.HttpMethod} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    string Rows(int from, int count) => "[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"Id\":" + i + ",\"Customer\":\"Café " + i + "\",\"Total\":" + (1000 - i) + ",\"Paid\":true}")) + "]";
                    var (status, answer) = !authorized ? (401, "{\"msg\":\"Authentication required - Invalid token\"}")
                        : (context.Request.HttpMethod, raw) switch
                        {
                            ("GET", "/api/v2/meta/bases/") => (200, "{\"list\":[{\"id\":\"p979g1063032uw4\",\"title\":\"Shop\"}],\"pageInfo\":{\"totalRows\":1}}"),
                            ("POST", _) => (200, "{\"Id\":43}"),
                            ("PATCH", _) or ("DELETE", _) => (200, "[{\"Id\":43}]"),
                            ("GET", _) when raw.Contains("/records/count", StringComparison.Ordinal) => (200, "{\"count\":150}"),
                            ("GET", _) when raw.Contains("offset=0&", StringComparison.Ordinal) => (200, "{\"list\":" + Rows(1, 100) + ",\"pageInfo\":{\"totalRows\":150,\"isLastPage\":false}}"),
                            ("GET", _) when raw.Contains("offset=100&", StringComparison.Ordinal) => (200, "{\"list\":" + Rows(101, 20) + ",\"pageInfo\":{\"totalRows\":150,\"isLastPage\":false}}"),
                            _ => (404, "{\"msg\":\"Not found\"}"),
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
