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
    /// Harvest DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Harvest's API v2 that refuses a call without the token, the account or a User-Agent: a
    /// Harvest connection made over the API with its account, its token vaulted, tested; a published pipeline creates a client
    /// with an accent, reads the running time entries over two pages - no more than asked - logs time, stops its timer, changes
    /// its notes and deletes it; the token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class HarvestDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Account = "123456";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_reads_logs_stops_changes_and_deletes_with_its_token()
        {
            var token = "ht" + Guid.NewGuid().ToString("N");
            using var harvest = new HarvestStandIn(token);
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
                    Name = "Shop time", ConnectorType = "harvest", Endpoint = $"http://127.0.0.1:{harvest.Port}/v2", TimeoutSeconds = 10,
                    Settings = new() { ["account_id"] = Account },
                    Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Time" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("client", "New client", "create_client", "fields = {\"name\": \"Café AB\", \"currency\": \"SEK\"}"),
                        Call("entries", "Running", "time_entries", "is_running = true\nfrom = 2026-10-01\nmax = 150"),
                        Call("log", "Log time", "create_time_entry", "fields = {\"project_id\": 7, \"task_id\": 13, \"spent_date\": \"2026-10-13\", \"hours\": 1.5}"),
                        Call("stop", "Stop", "stop_time_entry", "id = 42"),
                        Call("notes", "Notes", "update_time_entry", "id = 42\nfields = {\"notes\": \"Café design\"}"),
                        Call("delete", "Drop", "delete_time_entry", "id = 42"),
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
                var received = harvest.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token, account or agent: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /v2/company",
                    "POST /v2/clients {\"name\":\"Café AB\",\"currency\":\"SEK\"}",
                    "GET /v2/time_entries?is_running=true&from=2026-10-01&page=1&per_page=100",
                    "GET /v2/time_entries?is_running=true&from=2026-10-01&page=2&per_page=100",
                    "POST /v2/time_entries {\"project_id\":7,\"task_id\":13,\"spent_date\":\"2026-10-13\",\"hours\":1.5}",
                    "PATCH /v2/time_entries/42/stop",
                    "PATCH /v2/time_entries/42 {\"notes\":\"Café design\"}",
                    "DELETE /v2/time_entries/42",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("Café AB", JsonNode.Parse(Outputs("client"))!["Result"]![0]!["name"]!.GetValue<string>());
                Assert.Equal(150, JsonNode.Parse(Outputs("entries"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.False(JsonNode.Parse(Outputs("stop"))!["Result"]![0]!["is_running"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Harvest's API v2 that answers only a call carrying the token, the account and an agent.</summary>
        private sealed class HarvestStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _token;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public HarvestStandIn(string token)
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
                    var authorized = context.Request.Headers["Authorization"] == "Bearer " + _token && context.Request.Headers["Harvest-Account-Id"] == Account && !string.IsNullOrEmpty(context.Request.UserAgent);
                    lock (_gate)
                    {
                        _received.Add(($"{context.Request.HttpMethod} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    string Entries(int from, int count) => "[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"id\":" + i + ",\"notes\":\"Café " + i + "\",\"hours\":1.5,\"is_running\":true}")) + "]";
                    const string Entry = "{\"id\":42,\"notes\":\"Café design\",\"hours\":1.5,\"is_running\":false}";
                    var (status, answer) = !authorized ? (401, "{\"error\":\"invalid_token\",\"error_description\":\"The access token provided is invalid.\"}")
                        : (context.Request.HttpMethod, raw) switch
                        {
                            ("GET", "/v2/company") => (200, "{\"name\":\"Café AB\",\"base_uri\":\"https://cafe.harvestapp.com\"}"),
                            ("GET", _) when raw.Contains("page=2&", StringComparison.Ordinal) => (200, "{\"time_entries\":" + Entries(101, 80) + ",\"page\":2,\"next_page\":null}"),
                            ("GET", _) when raw.StartsWith("/v2/time_entries?", StringComparison.Ordinal) => (200, "{\"time_entries\":" + Entries(1, 100) + ",\"page\":1,\"next_page\":2}"),
                            ("POST", "/v2/clients") => (201, "{\"id\":12,\"name\":\"Café AB\",\"currency\":\"SEK\"}"),
                            ("POST", _) or ("PATCH", _) => (200, Entry),
                            ("DELETE", _) => (200, ""),
                            _ => (404, "{\"message\":\"Not found\"}"),
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
