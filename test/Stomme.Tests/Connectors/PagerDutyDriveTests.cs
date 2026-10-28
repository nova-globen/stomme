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
    /// BL-229 - PagerDuty DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for PagerDuty's REST API v2 that refuses a call without the token, and a write without its
    /// From: a PagerDuty connection made over the API with the e-mail its writes come from, its token vaulted, tested; a
    /// published pipeline creates an incident with an accent, reads the triggered incidents over two pages - no more than asked -
    /// resolves the incident, adds a note, reads its notes and who is on call; the token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PagerDutyDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_reads_resolves_and_notes_incidents_and_reads_who_is_on_call_with_its_token()
        {
            var token = "pd" + Guid.NewGuid().ToString("N");
            using var pagerDuty = new PagerDutyStandIn(token);
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
                    Name = "Shop on-call", ConnectorType = "pagerduty", Endpoint = $"http://127.0.0.1:{pagerDuty.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["from"] = "ops@example.test" },
                    Credentials = new() { ["api-key"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "On call" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("create", "Raise", "create_incident", "title = Kassan på café nere\nservice = PSRV1\nurgency = high\nincident_key = kassa/1"),
                        Call("read", "Open", "incidents", "statuses = triggered\nmax = 120"),
                        Call("resolve", "Resolve", "update_incident", "id = PINC9\nstatus = resolved\nresolution = Restarted"),
                        Call("note", "Note", "create_note", "id = PINC9\ncontent = Kassan är tillbaka\nfrom = ada@example.test"),
                        Call("notes", "Notes", "notes", "id = PINC9"),
                        Call("oncalls", "On call", "oncalls", "schedules = PSCH1"),
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
                var received = pagerDuty.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /abilities",
                    "POST /incidents from ops@example.test {\"incident\":{\"type\":\"incident\",\"title\":\"Kassan på café nere\",\"service\":{\"id\":\"PSRV1\",\"type\":\"service_reference\"},\"urgency\":\"high\",\"incident_key\":\"kassa/1\"}}",
                    "GET /incidents?statuses[]=triggered&offset=0&limit=100",
                    "GET /incidents?statuses[]=triggered&offset=100&limit=100",
                    "PUT /incidents/PINC9 from ops@example.test {\"incident\":{\"type\":\"incident_reference\",\"status\":\"resolved\",\"resolution\":\"Restarted\"}}",
                    "POST /incidents/PINC9/notes from ada@example.test {\"note\":{\"content\":\"Kassan är tillbaka\"}}",
                    "GET /incidents/PINC9/notes",
                    "GET /oncalls?schedule_ids[]=PSCH1&offset=0&limit=100",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("PINC9", JsonNode.Parse(Outputs("create"))!["Result"]![0]!["id"]!.GetValue<string>());
                Assert.Equal(120, JsonNode.Parse(Outputs("read"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal("resolved", JsonNode.Parse(Outputs("resolve"))!["Result"]![0]!["status"]!.GetValue<string>());
                Assert.Equal("Ada", JsonNode.Parse(Outputs("note"))!["Result"]![0]!["user"]!.GetValue<string>());
                Assert.Equal(1, JsonNode.Parse(Outputs("notes"))!["RecordCount"]!.GetValue<int>());
                Assert.Equal("Ada", JsonNode.Parse(Outputs("oncalls"))!["Result"]![0]!["user"]!.GetValue<string>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for PagerDuty's REST API v2 that answers only a call carrying the token - and a write its From.</summary>
        private sealed class PagerDutyStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _token;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public PagerDutyStandIn(string token)
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
                    var method = context.Request.HttpMethod;
                    var from = context.Request.Headers["From"];
                    var authorized = context.Request.Headers["Authorization"] == "Token token=" + _token
                        && (context.Request.Headers["Accept"] ?? "").Contains("application/vnd.pagerduty+json", StringComparison.Ordinal)
                        && (method == "GET" || from is not null);
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(from is null ? "" : " from " + from)}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    string Incidents(int start, int count, bool more) => "{\"incidents\":[" + string.Join(",", Enumerable.Range(start, count).Select(i => "{\"id\":\"PINC" + i + "\",\"title\":\"Kassan nere " + i + "\",\"status\":\"triggered\"}")) + "],\"more\":" + (more ? "true" : "false") + "}";
                    var path = raw.Split('?')[0];
                    var (status, answer) = !authorized ? (401, "{\"error\":{\"message\":\"Unauthorized\",\"code\":2006}}")
                        : (method, path) switch
                        {
                            ("GET", "/abilities") => (200, "{\"abilities\":[\"teams\",\"urgencies\"]}"),
                            ("POST", "/incidents") => (201, "{\"incident\":{\"id\":\"PINC9\",\"incident_number\":9,\"title\":\"Kassan på café nere\",\"status\":\"triggered\",\"service\":{\"id\":\"PSRV1\",\"type\":\"service_reference\",\"summary\":\"Kassa\"}}}"),
                            ("GET", "/incidents") => (200, raw.Contains("offset=100", StringComparison.Ordinal) ? Incidents(101, 80, false) : Incidents(1, 100, true)),
                            ("PUT", "/incidents/PINC9") => (200, "{\"incident\":{\"id\":\"PINC9\",\"status\":\"resolved\"}}"),
                            ("POST", "/incidents/PINC9/notes") => (201, "{\"note\":{\"id\":\"PNOTE1\",\"content\":\"Kassan är tillbaka\",\"user\":{\"id\":\"PUSR1\",\"type\":\"user_reference\",\"summary\":\"Ada\"}}}"),
                            ("GET", "/incidents/PINC9/notes") => (200, "{\"notes\":[{\"id\":\"PNOTE1\",\"content\":\"Kassan är tillbaka\"}]}"),
                            ("GET", "/oncalls") => (200, "{\"oncalls\":[{\"escalation_level\":1,\"user\":{\"id\":\"PUSR1\",\"type\":\"user_reference\",\"summary\":\"Ada\"}}],\"more\":false}"),
                            _ => (404, "{\"error\":{\"message\":\"Not Found\",\"code\":2100}}"),
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
