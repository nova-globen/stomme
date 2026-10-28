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
    /// BL-229 - Freshservice DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for a desk's API v2 that answers its API key alone. A Freshservice connection made over
    /// the API, its key vaulted, tested; a published pipeline creates a ticket, updates it, reads the urgent tickets by
    /// Freshservice's query over two pages no further than asked, gets a change and deletes the ticket; the key in neither the
    /// graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class FreshserviceDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_keeps_tickets_with_the_desks_api_key()
        {
            var key = "fs" + Guid.NewGuid().ToString("N");
            using var desk = new FreshserviceStandIn(key);
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
                    Name = "Kafé desk", ConnectorType = "freshservice", Endpoint = $"http://127.0.0.1:{desk.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé desk" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("report", "Report", "create_record", "resource = tickets\nvalues = {\"email\": \"bo@kafe.example.com\", \"subject\": \"Kassan är nere\", \"priority\": 3, \"status\": 2, \"cc_emails\": [\"cy@kafe.example.com\"]}"),
                        Call("escalate", "Escalate", "update_record", "resource = tickets\nid = 1042\nvalues = {\"priority\": 4}"),
                        Call("urgent", "Urgent", "records", "resource = tickets\nquery = priority:4 AND status:2\nmax = 150"),
                        Call("change", "Change", "record", "resource = changes\nid = 77"),
                        Call("drop", "Drop", "delete_record", "resource = tickets\nid = 1042"),
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
                var received = desk.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "refused: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /api/v2/tickets?per_page=1",
                    "POST /api/v2/tickets {\"email\":\"bo@kafe.example.com\",\"subject\":\"Kassan är nere\",\"priority\":3,\"status\":2,\"cc_emails\":[\"cy@kafe.example.com\"]}",
                    "PUT /api/v2/tickets/1042 {\"priority\":4}",
                    "GET /api/v2/tickets/filter?query=%22priority%3A4%20AND%20status%3A2%22&page=1&per_page=100",
                    "GET /api/v2/tickets/filter?query=%22priority%3A4%20AND%20status%3A2%22&page=2&per_page=100",
                    "GET /api/v2/changes/77",
                    "DELETE /api/v2/tickets/1042",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(1042, JsonNode.Parse(Outputs("report"))!["Result"]![0]!["id"]!.GetValue<long>());
                Assert.Equal(4, JsonNode.Parse(Outputs("escalate"))!["Result"]![0]!["priority"]!.GetValue<long>());
                Assert.Equal(150, JsonNode.Parse(Outputs("urgent"))!["RecordCount"]!.GetValue<int>()); // both pages, no further than asked
                Assert.Equal("Ny kassa", JsonNode.Parse(Outputs("change"))!["Result"]![0]!["subject"]!.GetValue<string>());
                Assert.True(JsonNode.Parse(Outputs("drop"))!["Result"]![0]!["deleted"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a Freshservice desk's API v2: its API key alone, as HTTP Basic with X for a password.</summary>
        private sealed class FreshserviceStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _authorization;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public FreshserviceStandIn(string key)
            {
                _authorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(key + ":X"));
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
                    var method = context.Request.HttpMethod;
                    var path = raw.Split('?')[0];
                    var authorized = context.Request.Headers["Authorization"] == _authorization;
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    static string Tickets(int first, int count) => "{\"tickets\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":" + i + ",\"subject\":\"Kassan " + i + "\",\"priority\":4,\"status\":2}")) + "]}";
                    var (status, answer) = !authorized ? (401, "{\"code\":\"invalid_credentials\",\"message\":\"You have to be logged in to perform this action.\"}")
                        : (method, path) switch
                        {
                            ("GET", "/api/v2/tickets") => (200, Tickets(1042, 1)),
                            ("POST", "/api/v2/tickets") => (201, "{\"ticket\":{\"id\":1042,\"subject\":\"Kassan är nere\",\"priority\":3,\"status\":2}}"),
                            ("PUT", "/api/v2/tickets/1042") => (200, "{\"ticket\":{\"id\":1042,\"subject\":\"Kassan är nere\",\"priority\":4,\"status\":2}}"),
                            ("GET", "/api/v2/tickets/filter") => (200, raw.Contains("page=2&", StringComparison.Ordinal) ? Tickets(101, 100) : Tickets(1, 100)),
                            ("GET", "/api/v2/changes/77") => (200, "{\"change\":{\"id\":77,\"subject\":\"Ny kassa\",\"status\":1}}"),
                            ("DELETE", "/api/v2/tickets/1042") => (204, ""),
                            _ => (404, "{\"code\":\"access_denied\",\"message\":\"You are not authorized to perform this action.\"}"),
                        };
                    context.Response.StatusCode = status;
                    if (answer.Length > 0)
                    {
                        var bytes = Encoding.UTF8.GetBytes(answer);
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
