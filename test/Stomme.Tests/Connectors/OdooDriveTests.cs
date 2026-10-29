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
    /// Odoo DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port, against
    /// a loopback stand-in for an Odoo instance's JSON-RPC: an Odoo connection made over the API, its password vaulted, tested (the
    /// contacts counted); a published pipeline makes a contact, reads the contacts a domain keeps over two pages, changes one and
    /// deletes it - the login asked for per step, every call execute_kw with the user's id; the password in neither the graph nor
    /// the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class OdooDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_makes_reads_changes_and_deletes_records()
        {
            var secret = "s" + Guid.NewGuid().ToString("N");
            using var odoo = new OdooStandIn();
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
                    Name = "ERP", ConnectorType = "odoo", Endpoint = $"http://127.0.0.1:{odoo.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["database"] = "prod_db" },
                    Credentials = new() { ["username"] = "ada@example.com", ["password"] = secret },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "ERP" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("make", "New contact", "create_record", "model = res.partner\nvalues = {\"name\": \"Café <b>Ada</b> & co\", \"is_company\": true}"),
                        Call("read", "Companies", "records", "model = res.partner\ndomain = [[\"is_company\", \"=\", true]]\nfields = name, country_id\nmax = 130"),
                        Call("change", "Phone", "update_records", "model = res.partner\nids = 7\nvalues = {\"phone\": \"+46 8 555\"}"),
                        Call("drop", "Drop", "delete_records", "model = res.partner\nids = 7"),
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
                var calls = odoo.Received.Select(body => JsonNode.Parse(body)!["params"]!).Where(call => call["method"]!.GetValue<string>() == "execute_kw").Select(call => call["args"]!).ToList();
                Assert.All(calls, args => Assert.Equal(("prod_db", 7L, secret), (args[0]!.GetValue<string>(), args[1]!.GetValue<long>(), args[2]!.GetValue<string>())));
                Assert.Equal(new[]
                {
                    "res.partner search_count [[]] {}",
                    "res.partner create [{\"name\":\"Café <b>Ada</b> & co\",\"is_company\":true}] {}",
                    "res.partner search_read [[[\"is_company\",\"=\",true]]] {\"fields\":[\"name\",\"country_id\"],\"offset\":0,\"limit\":100}",
                    "res.partner search_read [[[\"is_company\",\"=\",true]]] {\"fields\":[\"name\",\"country_id\"],\"offset\":100,\"limit\":30}",
                    "res.partner write [[7],{\"phone\":\"+46 8 555\"}] {}",
                    "res.partner unlink [[7]] {}",
                }, calls.Select(args => $"{args[3]} {args[4]} {args[5]!.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })} {args[6]!.ToJsonString()}"));
                Assert.Equal(5, odoo.Received.Count(body => body.Contains("\"login\"", StringComparison.Ordinal))); // the test and each step signed in

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var read2 = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "read")!;
                Assert.Contains("\"RecordCount\": 130", read2["outputsJson"]!.GetValue<string>()); // both pages, up to the most asked for
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for an Odoo instance's JSON-RPC: a login, a count, a contact made, the companies over two pages, one written and unlinked.</summary>
        private sealed class OdooStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<string> _received = new();

            public OdooStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<string> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private static string Partners(int from, int count) =>
                "[" + string.Join(",", Enumerable.Range(from, count).Select(n => "{\"id\":" + n + ",\"name\":\"Company " + n + "\",\"country_id\":[205,\"Sweden\"]}")) + "]";

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

                    lock (_gate)
                    {
                        _received.Add(body);
                    }

                    var call = context.Request.Url!.AbsolutePath == "/jsonrpc" ? JsonNode.Parse(body)?["params"] : null;
                    var args = call?["args"];
                    var result = call?["method"]?.GetValue<string>() switch
                    {
                        "login" => "7",
                        "execute_kw" => args?[4]?.GetValue<string>() switch
                        {
                            "search_count" => "42",
                            "create" => "99",
                            "search_read" => args?[6]?["offset"]?.GetValue<int>() == 100 ? Partners(101, 40) : Partners(1, 100),
                            _ => "true",
                        },
                        _ => null,
                    };
                    context.Response.StatusCode = result is null ? 404 : 200;
                    var bytes = Encoding.UTF8.GetBytes(result is null ? "Not found" : "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":" + result + "}");
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
