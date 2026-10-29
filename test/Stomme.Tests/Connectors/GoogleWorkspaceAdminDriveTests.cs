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
    /// Google Workspace Admin DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the
    /// extraction port, against a loopback stand-in for Google's token endpoint and Directory API: the refresh token exchanged
    /// for an access token, the API answering that token alone. A connection made over the API, its secrets vaulted, tested; a
    /// published pipeline reads the users over two pages by Google's token - no more than asked - suspends one by a PATCH, adds
    /// them to a group, creates a group with an accent and disables a ChromeOS device; the secrets in neither the graph nor the
    /// run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GoogleWorkspaceAdminDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_reads_users_and_keeps_groups_and_devices_with_its_refresh_token()
        {
            var refresh = "1//" + Guid.NewGuid().ToString("N");
            var secret = "GOCSPX-" + Guid.NewGuid().ToString("N");
            using var google = new GoogleStandIn(refresh);
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
                    Name = "Kafé Workspace", ConnectorType = "googleworkspaceadmin", Endpoint = $"http://127.0.0.1:{google.Port}/admin/directory/v1", TimeoutSeconds = 10,
                    Settings = new() { ["token_url"] = $"http://127.0.0.1:{google.Port}/token" },
                    Credentials = new() { ["client-id"] = "client-drive", ["client-secret"] = secret, ["refresh-token"] = refresh },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé directory" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("users", "Users", "users", "query = isSuspended=false\nmax = 120"),
                        Call("suspend", "Suspend", "update_user", "user = ada@kafe.example.com\nsuspended = true"),
                        Call("join", "Join", "add_to_group", "user = ada@kafe.example.com\ngroup = fika@kafe.example.com\nrole = manager"),
                        Call("group", "Group", "create_group", "email = kassa@kafe.example.com\nname = Kassa & bröd"),
                        Call("device", "Device", "device_action", "device = d1\naction = disable"),
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
                var received = google.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "refused: " + request.Line));
                Assert.Equal("POST /token", received[0].Line); // the refresh token for an access token - the rest with it
                Assert.Equal(new[]
                {
                    "GET /admin/directory/v1/customers/my_customer",
                    "GET /admin/directory/v1/users?customer=my_customer&query=isSuspended%3Dfalse&maxResults=120",
                    "GET /admin/directory/v1/users?customer=my_customer&query=isSuspended%3Dfalse&maxResults=20&pageToken=p1",
                    "PATCH /admin/directory/v1/users/ada%40kafe.example.com {\"suspended\":true}",
                    "POST /admin/directory/v1/groups/fika%40kafe.example.com/members {\"email\":\"ada@kafe.example.com\",\"role\":\"MANAGER\"}",
                    "POST /admin/directory/v1/groups {\"email\":\"kassa@kafe.example.com\",\"name\":\"Kassa & bröd\"}",
                    "POST /admin/directory/v1/customer/my_customer/devices/chromeos/d1/action {\"action\":\"disable\"}",
                }, received.Where(request => !request.Line.StartsWith("POST /token", StringComparison.Ordinal)).Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(120, JsonNode.Parse(Outputs("users"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.True(JsonNode.Parse(Outputs("suspend"))!["Result"]![0]!["suspended"]!.GetValue<bool>());
                Assert.Equal("MANAGER", JsonNode.Parse(Outputs("join"))!["Result"]![0]!["role"]!.GetValue<string>());
                Assert.True(JsonNode.Parse(Outputs("device"))!["Result"]![0]!["disabled"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                foreach (var hidden in new[] { refresh, secret })
                {
                    Assert.DoesNotContain(hidden, steps, StringComparison.Ordinal);
                    Assert.DoesNotContain(hidden, graphBody, StringComparison.Ordinal);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Google's token endpoint and Directory API: an access token for the refresh token alone, the API for that token alone.</summary>
        private sealed class GoogleStandIn : IDisposable
        {
            private const string Token = "ya29.drive";
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _refresh;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public GoogleStandIn(string refresh)
            {
                _refresh = refresh;
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
                    var issuing = method == "POST" && path == "/token";
                    var authorized = issuing ? body.Contains("refresh_token=" + Uri.EscapeDataString(_refresh), StringComparison.Ordinal) : context.Request.Headers["Authorization"] == "Bearer " + Token;
                    lock (_gate)
                    {
                        // The token request's form carries the secrets: logged by its line alone.
                        _received.Add(($"{method} {raw}{(issuing || body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    static string Users(int first, int count, string? next) => "{\"users\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":\"1" + i.ToString("D9") + "\",\"primaryEmail\":\"user" + i + "@kafe.example.com\"}")) + "]" + (next is null ? "" : ",\"nextPageToken\":\"" + next + "\"") + "}";
                    var (status, answer) = !authorized ? (issuing ? 400 : 401, issuing ? "{\"error\":\"invalid_grant\"}" : "{\"error\":{\"code\":401,\"message\":\"Request had invalid authentication credentials.\",\"status\":\"UNAUTHENTICATED\"}}")
                        : (method, path) switch
                        {
                            ("POST", "/token") => (200, "{\"access_token\":\"" + Token + "\",\"expires_in\":3600,\"token_type\":\"Bearer\"}"),
                            ("GET", "/admin/directory/v1/customers/my_customer") => (200, "{\"id\":\"C01abc23d\",\"customerDomain\":\"kafe.example.com\"}"),
                            ("GET", "/admin/directory/v1/users") => (200, raw.Contains("pageToken=p1", StringComparison.Ordinal) ? Users(101, 60, null) : Users(1, 100, "p1")),
                            ("PATCH", "/admin/directory/v1/users/ada%40kafe.example.com") => (200, "{\"id\":\"1000000001\",\"primaryEmail\":\"ada@kafe.example.com\",\"suspended\":true}"),
                            ("POST", "/admin/directory/v1/groups/fika%40kafe.example.com/members") => (200, "{\"id\":\"1000000001\",\"email\":\"ada@kafe.example.com\",\"role\":\"MANAGER\",\"type\":\"USER\"}"),
                            ("POST", "/admin/directory/v1/groups") => (200, "{\"id\":\"g2\",\"email\":\"kassa@kafe.example.com\",\"name\":\"Kassa & bröd\"}"),
                            ("POST", "/admin/directory/v1/customer/my_customer/devices/chromeos/d1/action") => (204, ""),
                            _ => (404, "{\"error\":{\"code\":404,\"message\":\"Not Found\",\"status\":\"NOT_FOUND\"}}"),
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
