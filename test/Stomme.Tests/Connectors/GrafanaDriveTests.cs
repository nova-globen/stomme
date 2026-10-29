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
    /// Grafana DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for a Grafana server that refuses a call without the token: a Grafana connection made over
    /// the API, its token vaulted, tested; a published pipeline reads the dashboards over two pages - no more than asked -
    /// renames one with its panels kept, creates a team with an accent, adds a member and gives a user a role; the token in
    /// neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GrafanaDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_reads_renames_dashboards_and_keeps_teams_with_its_token()
        {
            var token = "glsa_" + Guid.NewGuid().ToString("N");
            using var grafana = new GrafanaStandIn(token);
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
                    Name = "Shop dashboards", ConnectorType = "grafana", Endpoint = $"http://127.0.0.1:{grafana.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Fika dashboards" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("read", "Read", "dashboards", "query = fika\nmax = 120"),
                        Call("rename", "Rename", "update_dashboard", "dashboard = abc123\ntitle = Försäljning"),
                        Call("team", "Team", "create_team", "name = Fika & bröd"),
                        Call("member", "Member", "add_team_member", "team = 5\nuser = 3"),
                        Call("role", "Role", "update_user", "user = 3\nrole = Editor"),
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
                var received = grafana.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /api/org",
                    "GET /api/search?type=dash-db&query=fika&limit=100&page=1",
                    "GET /api/search?type=dash-db&query=fika&limit=100&page=2",
                    "GET /api/dashboards/uid/abc123",
                    "POST /api/dashboards/db {\"dashboard\":{\"id\":7,\"uid\":\"abc123\",\"title\":\"Försäljning\",\"version\":4,\"panels\":[{\"id\":1,\"type\":\"timeseries\"}]},\"folderUid\":\"f1\",\"overwrite\":false}",
                    "POST /api/teams {\"name\":\"Fika & bröd\"}",
                    "POST /api/teams/5/members {\"userId\":3}",
                    "PATCH /api/org/users/3 {\"role\":\"Editor\"}",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(120, JsonNode.Parse(Outputs("read"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal(5, JsonNode.Parse(Outputs("rename"))!["Result"]![0]!["version"]!.GetValue<long>());
                Assert.Equal(5, JsonNode.Parse(Outputs("team"))!["Result"]![0]!["id"]!.GetValue<long>());
                Assert.True(JsonNode.Parse(Outputs("member"))!["Result"]![0]!["added"]!.GetValue<bool>());
                Assert.Equal("Editor", JsonNode.Parse(Outputs("role"))!["Result"]![0]!["role"]!.GetValue<string>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a Grafana server that answers only a call carrying the token.</summary>
        private sealed class GrafanaStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _token;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public GrafanaStandIn(string token)
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
                    var authorized = context.Request.Headers["Authorization"] == "Bearer " + _token;
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    static string Dashboards(int first, int count) => "[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":" + i + ",\"uid\":\"d" + i + "\",\"title\":\"Fika " + i + "\"}")) + "]";
                    var path = raw.Split('?')[0];
                    var (status, answer) = !authorized ? (401, "{\"message\":\"Invalid API key\"}")
                        : (method, path) switch
                        {
                            ("GET", "/api/org") => (200, "{\"id\":1,\"name\":\"Kafé\"}"),
                            ("GET", "/api/search") => (200, raw.EndsWith("page=2", StringComparison.Ordinal) ? Dashboards(101, 80) : Dashboards(1, 100)),
                            ("GET", "/api/dashboards/uid/abc123") => (200, "{\"dashboard\":{\"id\":7,\"uid\":\"abc123\",\"title\":\"Sales\",\"version\":4,\"panels\":[{\"id\":1,\"type\":\"timeseries\"}]},\"meta\":{\"folderUid\":\"f1\"}}"),
                            ("POST", "/api/dashboards/db") => (200, "{\"id\":7,\"uid\":\"abc123\",\"status\":\"success\",\"version\":5}"),
                            ("POST", "/api/teams") => (200, "{\"message\":\"Team created\",\"teamId\":5}"),
                            ("POST", "/api/teams/5/members") => (200, "{\"message\":\"Member added to Team\"}"),
                            ("PATCH", "/api/org/users/3") => (200, "{\"message\":\"Organization user updated\"}"),
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
