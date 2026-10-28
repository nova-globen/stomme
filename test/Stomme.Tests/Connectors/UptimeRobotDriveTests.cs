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
    /// BL-229 - UptimeRobot DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for UptimeRobot's API that refuses a call without the key in its form: an UptimeRobot
    /// connection made over the API, its key vaulted, tested; a published pipeline creates a monitor with an accent, reads the
    /// monitors over three pages by offset - no more than asked - pauses one, creates a weekly maintenance window and reads
    /// the alert contacts; the key in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class UptimeRobotDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_reads_and_pauses_monitors_with_its_key()
        {
            var key = "u" + Guid.NewGuid().ToString("N");
            using var robot = new RobotStandIn(key);
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
                    Name = "Shop uptime", ConnectorType = "uptimerobot", Endpoint = $"http://127.0.0.1:{robot.Port}/v2", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Fika uptime" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("create", "Create", "create_monitor", "friendly_name = Kassa & bröd\nurl = https://kafe.example.test\ntype = http\ninterval = 300"),
                        Call("read", "Read", "monitors", "statuses = up,down\nmax = 120"),
                        Call("pause", "Pause", "update_monitor", "id = 777001\nstatus = paused"),
                        Call("window", "Window", "create_maintenance_window", "friendly_name = Fika\ntype = weekly\nvalue = 1-3\nstart_time = 22:00\nduration = 60"),
                        Call("contacts", "Contacts", "alert_contacts", ""),
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
                var received = robot.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no key: " + request.Line));
                Assert.Equal(new[]
                {
                    "POST /v2/getAccountDetails",
                    "POST /v2/newMonitor friendly_name=Kassa+%26+br%C3%B6d&url=https%3A%2F%2Fkafe.example.test&type=1&interval=300",
                    "POST /v2/getMonitors statuses=2-9&offset=0&limit=50",
                    "POST /v2/getMonitors statuses=2-9&offset=50&limit=50",
                    "POST /v2/getMonitors statuses=2-9&offset=100&limit=50",
                    "POST /v2/editMonitor id=777001&status=0",
                    "POST /v2/newMWindow friendly_name=Fika&type=3&value=1-3&start_time=22%3A00&duration=60",
                    "POST /v2/getAlertContacts offset=0&limit=50",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(777810874, JsonNode.Parse(Outputs("create"))!["Result"]![0]!["id"]!.GetValue<long>());
                Assert.Equal(120, JsonNode.Parse(Outputs("read"))!["RecordCount"]!.GetValue<int>()); // three pages, no more than asked
                Assert.Equal(777001, JsonNode.Parse(Outputs("pause"))!["Result"]![0]!["id"]!.GetValue<long>());
                Assert.Equal(581, JsonNode.Parse(Outputs("window"))!["Result"]![0]!["id"]!.GetValue<long>());
                Assert.Equal("ada@kafe.example.test", JsonNode.Parse(Outputs("contacts"))!["Result"]![0]!["value"]!.GetValue<string>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for UptimeRobot's API v2 that answers only a call carrying the key in its form.</summary>
        private sealed class RobotStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _key;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public RobotStandIn(string key)
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

                    var path = context.Request.RawUrl ?? "";
                    var prefix = "api_key=" + _key + "&format=json";
                    var authorized = context.Request.HttpMethod == "POST" && body.StartsWith(prefix, StringComparison.Ordinal);
                    var rest = authorized && body.Length > prefix.Length ? " " + body[(prefix.Length + 1)..] : "";
                    lock (_gate)
                    {
                        _received.Add(($"{context.Request.HttpMethod} {path}{rest}", authorized));
                    }

                    static string Monitors(int first, int count) => "{\"stat\":\"ok\",\"pagination\":{\"offset\":" + (first - 1) + ",\"limit\":50,\"total\":180},\"monitors\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":" + (777000 + i) + ",\"friendly_name\":\"Kassa " + i + "\",\"status\":2}")) + "]}";
                    var (status, answer) = !authorized ? (200, "{\"stat\":\"fail\",\"error\":{\"type\":\"invalid_parameter\",\"message\":\"api_key is wrong.\"}}")
                        : path switch
                        {
                            "/v2/getAccountDetails" => (200, "{\"stat\":\"ok\",\"account\":{\"email\":\"ada@kafe.example.test\",\"monitor_limit\":50}}"),
                            "/v2/newMonitor" => (200, "{\"stat\":\"ok\",\"monitor\":{\"id\":777810874,\"status\":1}}"),
                            "/v2/getMonitors" => (200, body.Contains("offset=100", StringComparison.Ordinal) ? Monitors(101, 50) : body.Contains("offset=50", StringComparison.Ordinal) ? Monitors(51, 50) : Monitors(1, 50)),
                            "/v2/editMonitor" => (200, "{\"stat\":\"ok\",\"monitor\":{\"id\":777001}}"),
                            "/v2/newMWindow" => (200, "{\"stat\":\"ok\",\"mwindow\":{\"id\":581,\"status\":1}}"),
                            "/v2/getAlertContacts" => (200, "{\"stat\":\"ok\",\"offset\":0,\"limit\":50,\"total\":1,\"alert_contacts\":[{\"id\":\"0993765\",\"type\":2,\"value\":\"ada@kafe.example.test\"}]}"),
                            _ => (404, "{\"stat\":\"fail\",\"error\":{\"type\":\"not_found\",\"message\":\"Not found.\"}}"),
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
