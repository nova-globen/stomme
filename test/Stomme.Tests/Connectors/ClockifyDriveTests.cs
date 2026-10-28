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
    /// BL-229 - Clockify DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Clockify's API v1 that refuses a call without the key: a Clockify connection made over the
    /// API, its key vaulted, tested; a published pipeline creates a client with an accent, reads the projects over two pages - no
    /// more than asked - logs time in UTC, reads the key's own user's time entries, moves the entry's end keeping its start and
    /// deletes it; the key in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class ClockifyDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Ws = "5f1a2b3c4d5e6f7081920a3b";
        private const string Entry = "60dd1b2c3d4e5f6071829304";
        private const string User = "60ee1b2c3d4e5f6071829304";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_reads_logs_moves_and_deletes_with_its_key()
        {
            var key = "ck" + Guid.NewGuid().ToString("N");
            using var clockify = new ClockifyStandIn(key);
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
                    Name = "Shop hours", ConnectorType = "clockify", Endpoint = $"http://127.0.0.1:{clockify.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = key },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Hours" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("client", "New client", "create_client", $"workspace = {Ws}\nname = Café AB\nfields = {{\"note\": \"Retainer\"}}"),
                        Call("projects", "Projects", "projects", $"workspace = {Ws}\narchived = false\nmax = 250"),
                        Call("log", "Log time", "create_time_entry", $"workspace = {Ws}\nstart = 2026-10-13T10:00:00+02:00\nend = 2026-10-13T12:00:00+02:00\nfields = {{\"description\": \"Café design\"}}"),
                        Call("entries", "My time", "time_entries", $"workspace = {Ws}\nstart = 2026-10-13T00:00:00Z"),
                        Call("move", "Move end", "update_time_entry", $"workspace = {Ws}\ntime_entry = {Entry}\nend = 2026-10-13T13:00:00+02:00"),
                        Call("delete", "Drop", "delete_time_entry", $"workspace = {Ws}\ntime_entry = {Entry}"),
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
                var received = clockify.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no key: " + request.Line));
                var ws = $"/api/v1/workspaces/{Ws}";
                Assert.Equal(new[]
                {
                    "GET /api/v1/user",
                    $"POST {ws}/clients {{\"note\":\"Retainer\",\"name\":\"Café AB\"}}",
                    $"GET {ws}/projects?archived=false&page=1&page-size=200",
                    $"GET {ws}/projects?archived=false&page=2&page-size=200",
                    $"POST {ws}/time-entries {{\"description\":\"Café design\",\"start\":\"2026-10-13T08:00:00Z\",\"end\":\"2026-10-13T10:00:00Z\"}}",
                    "GET /api/v1/user",
                    $"GET {ws}/user/{User}/time-entries?start=2026-10-13T00%3A00%3A00Z&page=1&page-size=200",
                    $"GET {ws}/time-entries/{Entry}",
                    $"PUT {ws}/time-entries/{Entry} {{\"end\":\"2026-10-13T11:00:00Z\",\"start\":\"2026-10-13T08:00:00Z\"}}",
                    $"DELETE {ws}/time-entries/{Entry}",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("Café AB", JsonNode.Parse(Outputs("client"))!["Result"]![0]!["name"]!.GetValue<string>());
                Assert.Equal(250, JsonNode.Parse(Outputs("projects"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal(Entry, JsonNode.Parse(Outputs("entries"))!["Result"]![0]!["id"]!.GetValue<string>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Clockify's API v1 that answers only a call carrying the key.</summary>
        private sealed class ClockifyStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _key;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public ClockifyStandIn(string key)
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
                    var authorized = context.Request.Headers["X-Api-Key"] == _key;
                    lock (_gate)
                    {
                        _received.Add(($"{context.Request.HttpMethod} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    string Projects(int from, int count) => "[" + string.Join(",", Enumerable.Range(from, count).Select(i => "{\"id\":\"" + i.ToString("x24") + "\",\"name\":\"Projekt " + i + "\",\"archived\":false}")) + "]";
                    var entry = "{\"id\":\"" + Entry + "\",\"description\":\"Café design\",\"timeInterval\":{\"start\":\"2026-10-13T08:00:00Z\",\"end\":\"2026-10-13T10:00:00Z\"}}";
                    var (status, answer) = !authorized ? (401, "{\"message\":\"Full authentication is required to access this resource\",\"code\":1000}")
                        : (context.Request.HttpMethod, raw) switch
                        {
                            ("GET", "/api/v1/user") => (200, "{\"id\":\"" + User + "\",\"name\":\"Shop\",\"activeWorkspace\":\"" + Ws + "\"}"),
                            ("GET", _) when raw.Contains("/projects?", StringComparison.Ordinal) && raw.Contains("page=2&", StringComparison.Ordinal) => (200, Projects(201, 80)),
                            ("GET", _) when raw.Contains("/projects?", StringComparison.Ordinal) => (200, Projects(1, 200)),
                            ("GET", _) when raw.Contains("/user/", StringComparison.Ordinal) => (200, "[" + entry + "]"),
                            ("GET", _) when raw.Contains("/time-entries/", StringComparison.Ordinal) => (200, entry),
                            ("POST", _) when raw.EndsWith("/clients", StringComparison.Ordinal) => (201, "{\"id\":\"60aa1b2c3d4e5f6071829304\",\"name\":\"Café AB\",\"note\":\"Retainer\"}"),
                            ("POST", _) or ("PUT", _) => (201, entry),
                            ("DELETE", _) => (200, entry),
                            _ => (404, "{\"message\":\"Not found\",\"code\":404}"),
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
