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
    /// Grist DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Grist's REST API that refuses a call without the key: a Grist connection made over the
    /// API, its key vaulted, tested; a published pipeline creates a record with an accent, reads a table's paid records - no
    /// more than asked - updates the record, upserts it by a match, counts with SQL and deletes the record; the key in neither
    /// the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GristDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Doc = "fSLRDYaF3BWXAE6kNXF4bS";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_reads_updates_upserts_queries_and_deletes_with_its_key()
        {
            var key = "gk" + Guid.NewGuid().ToString("N");
            using var grist = new GristStandIn(key);
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
                    Name = "Shop sheets", ConnectorType = "grist", Endpoint = $"http://127.0.0.1:{grist.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = key },
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
                        Call("create", "New order", "create_record", $"doc = {Doc}\ntable = Orders\nfields = {{\"Customer\": \"Café Ada\", \"Total\": 99.5, \"Paid\": false}}"),
                        Call("read", "Paid orders", "records", $"doc = {Doc}\ntable = Orders\nfilter = {{\"Paid\": true}}\nsort = -Total\nmax = 120"),
                        Call("update", "Paid", "update_record", $"doc = {Doc}\ntable = Orders\nid = 43\nfields = {{\"Paid\": true}}"),
                        Call("upsert", "Total", "upsert_record", $"doc = {Doc}\ntable = Orders\nmatch = {{\"Customer\": \"Café Ada\"}}\nfields = {{\"Total\": 120}}"),
                        Call("count", "How many", "sql", $"doc = {Doc}\nquery = select count(*) as n from Orders where Paid = ?\nargs = [1]"),
                        Call("delete", "Drop", "delete_records", $"doc = {Doc}\ntable = Orders\nids = 43"),
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
                var received = grist.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no key: " + request.Line));
                var records = $"/api/docs/{Doc}/tables/Orders/records";
                Assert.Equal(new[]
                {
                    "GET /api/orgs",
                    $"POST {records} {{\"records\":[{{\"fields\":{{\"Customer\":\"Café Ada\",\"Total\":99.5,\"Paid\":false}}}}]}}",
                    $"GET {records}?filter=%7B%22Paid%22%3A%5Btrue%5D%7D&sort=-Total&limit=120",
                    $"PATCH {records} {{\"records\":[{{\"id\":43,\"fields\":{{\"Paid\":true}}}}]}}",
                    $"PUT {records} {{\"records\":[{{\"require\":{{\"Customer\":\"Café Ada\"}},\"fields\":{{\"Total\":120}}}}]}}",
                    $"POST /api/docs/{Doc}/sql {{\"sql\":\"select count(*) as n from Orders where Paid = ?\",\"args\":[1]}}",
                    $"POST {records}/delete [43]",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(43, JsonNode.Parse(Outputs("create"))!["Result"]![0]!["id"]!.GetValue<int>()); // the id Grist gave
                Assert.Equal(120, JsonNode.Parse(Outputs("read"))!["RecordCount"]!.GetValue<int>()); // no more than asked
                Assert.Equal(150, JsonNode.Parse(Outputs("count"))!["Result"]![0]!["n"]!.GetValue<int>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Grist's REST API that answers only a call carrying the key.</summary>
        private sealed class GristStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _key;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public GristStandIn(string key)
            {
                _key = key;
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
                    var authorized = context.Request.Headers["Authorization"] == "Bearer " + _key;
                    lock (_gate)
                    {
                        _received.Add(($"{context.Request.HttpMethod} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    // Every paid record, whatever the limit - the connector stops at what it was asked for.
                    var rows = "[" + string.Join(",", Enumerable.Range(1, 150).Select(i => "{\"id\":" + i + ",\"fields\":{\"Customer\":\"Café " + i + "\",\"Total\":" + (1000 - i) + ",\"Paid\":true}}")) + "]";
                    var (status, answer) = !authorized ? (401, "{\"error\":\"Bad API key\"}")
                        : (context.Request.HttpMethod, raw) switch
                        {
                            ("GET", "/api/orgs") => (200, "[{\"id\":42,\"name\":\"Personal\",\"domain\":\"docs-42\"}]"),
                            ("POST", _) when raw.EndsWith("/sql", StringComparison.Ordinal) => (200, "{\"statement\":\"select count(*) as n from Orders where Paid = ?\",\"records\":[{\"fields\":{\"n\":150}}]}"),
                            ("POST", _) when raw.EndsWith("/records/delete", StringComparison.Ordinal) => (200, ""),
                            ("POST", _) => (200, "{\"records\":[{\"id\":43}]}"),
                            ("PATCH", _) or ("PUT", _) => (200, ""),
                            ("GET", _) when raw.Contains("/records?", StringComparison.Ordinal) => (200, "{\"records\":" + rows + "}"),
                            _ => (404, "{\"error\":\"Not found\"}"),
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
