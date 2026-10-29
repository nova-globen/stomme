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
    /// Okta DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for an Okta org that refuses a call without the token: an Okta connection made over the API,
    /// its token vaulted, tested; a published pipeline reads the users over two pages by the cursor Okta's Link header names
    /// though its address names another host - no more than asked - creates a user with an accent, unactivated, adds them to
    /// a group, deactivates and deletes them; the token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class OktaDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string UserId = "00u1a2b3c4d5e6f7g8h9";
        private const string GroupId = "00g9h8g7f6e5d4c3b2a1";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_reads_users_by_oktas_cursor_and_keeps_one_through_its_life_with_its_token()
        {
            var token = "00" + Guid.NewGuid().ToString("N");
            using var okta = new OktaStandIn(token);
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
                    Name = "Kafé directory", ConnectorType = "okta", Endpoint = $"http://127.0.0.1:{okta.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé staff" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("read", "Read", "users", "q = Ada\nmax = 120"),
                        Call("create", "Create", "create_user", "first_name = Åsa\nlast_name = Öberg\nemail = asa@kafe.example.test\nlogin = asa@kafe.example.test\ndepartment = Fika & bröd\nactivate = false"),
                        Call("member", "Member", "add_to_group", $"group = {GroupId}\nuser = {UserId}"),
                        Call("deactivate", "Deactivate", "deactivate_user", $"user = {UserId}"),
                        Call("delete", "Delete", "delete_user", $"user = {UserId}"),
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
                var received = okta.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /api/v1/users/me",
                    "GET /api/v1/users?q=Ada&limit=120",
                    "GET /api/v1/users?q=Ada&limit=120&after=c1",
                    "POST /api/v1/users?activate=false {\"profile\":{\"firstName\":\"Åsa\",\"lastName\":\"Öberg\",\"email\":\"asa@kafe.example.test\",\"login\":\"asa@kafe.example.test\",\"department\":\"Fika & bröd\"}}",
                    $"PUT /api/v1/groups/{GroupId}/users/{UserId}",
                    $"POST /api/v1/users/{UserId}/lifecycle/deactivate?sendEmail=false",
                    $"DELETE /api/v1/users/{UserId}?sendEmail=false",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(120, JsonNode.Parse(Outputs("read"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal("STAGED", JsonNode.Parse(Outputs("create"))!["Result"]![0]!["status"]!.GetValue<string>());
                Assert.True(JsonNode.Parse(Outputs("member"))!["Result"]![0]!["added"]!.GetValue<bool>());
                Assert.True(JsonNode.Parse(Outputs("deactivate"))!["Result"]![0]!["deactivated"]!.GetValue<bool>());
                Assert.True(JsonNode.Parse(Outputs("delete"))!["Result"]![0]!["deleted"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for an Okta org that answers only a call carrying the token in its SSWS header.</summary>
        private sealed class OktaStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _token;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public OktaStandIn(string token)
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
                    var authorized = context.Request.Headers["Authorization"] == "SSWS " + _token;
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    static string Users(int first, int count) => "[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":\"00u" + i.ToString("D17") + "\",\"status\":\"ACTIVE\",\"profile\":{\"firstName\":\"Ada\",\"lastName\":\"Lovelace " + i + "\",\"login\":\"ada" + i + "@kafe.example.test\"}}")) + "]";
                    var path = raw.Split('?')[0];
                    string? link = null;
                    var (status, answer) = !authorized ? (401, "{\"errorCode\":\"E0000011\",\"errorSummary\":\"Invalid token provided\",\"errorCauses\":[]}")
                        : (method, path) switch
                        {
                            ("GET", "/api/v1/users/me") => (200, "{\"id\":\"00uadmin0000000000000\",\"profile\":{\"login\":\"admin@kafe.example.test\"}}"),
                            ("GET", "/api/v1/users") when raw.Contains("after=c1", StringComparison.Ordinal) => (200, Users(101, 80)),
                            ("GET", "/api/v1/users") => (200, Users(1, 100)),
                            ("POST", "/api/v1/users") => (200, "{\"id\":\"" + UserId + "\",\"status\":\"STAGED\",\"profile\":{\"firstName\":\"Åsa\",\"login\":\"asa@kafe.example.test\"}}"),
                            ("PUT", _) or ("DELETE", _) => (204, ""),
                            ("POST", _) when path.EndsWith("/lifecycle/deactivate", StringComparison.Ordinal) => (200, "{}"),
                            _ => (404, "{\"errorCode\":\"E0000007\",\"errorSummary\":\"Not found: Resource not found\",\"errorCauses\":[]}"),
                        };
                    if (authorized && method == "GET" && path == "/api/v1/users" && !raw.Contains("after=", StringComparison.Ordinal))
                    {
                        // The next page's address on another host: followed, it would show.
                        link = "<http://elsewhere.example.test/api/v1/users?after=c1&limit=120>; rel=\"next\"";
                    }

                    context.Response.StatusCode = status;
                    if (link is not null)
                    {
                        context.Response.AddHeader("Link", link);
                    }

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
