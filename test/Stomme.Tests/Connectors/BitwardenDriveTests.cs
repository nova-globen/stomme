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
    /// Bitwarden DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for a self-hosted Bitwarden server: its identity endpoint issues a token for the
    /// organization's client credentials alone, its public API answers that token alone. A Bitwarden connection made over the
    /// API, its secret vaulted, tested; a published pipeline reads the members, creates a group with an accent, renames it with
    /// its collections kept, sets its members, invites a member and reads the events over two pages by Bitwarden's
    /// continuation token; the secret in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class BitwardenDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string GroupId = "3fa85f64-5717-4562-b3fc-2c963f66afa6";
        private const string MemberId = "6f9619ff-8b86-d011-b42d-00c04fc964ff";
        private const string CollectionId = "0f8fad5b-d9cb-469f-a165-70867728950e";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_keeps_groups_and_members_and_reads_the_events_with_the_organizations_key()
        {
            var secret = "bw-" + Guid.NewGuid().ToString("N");
            using var bitwarden = new BitwardenStandIn("organization.kafe-1", secret);
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
                    Name = "Kafé vault", ConnectorType = "bitwarden", Endpoint = $"http://127.0.0.1:{bitwarden.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["client-id"] = "organization.kafe-1", ["client-secret"] = secret },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé vault access" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("members", "Members", "members", "max = 50"),
                        Call("group", "Group", "create_group", $"name = Fika & bröd\ncollections = {CollectionId}:read-only"),
                        Call("rename", "Rename", "update_group", $"id = {GroupId}\nname = Kassan"),
                        Call("join", "Join", "set_group_members", $"id = {GroupId}\nmembers = {MemberId}"),
                        Call("invite", "Invite", "invite_member", "email = cy@kafe.example.test\nrole = manager"),
                        Call("events", "Events", "events", "start = 2026-10-01T00:00:00Z\nmax = 120"),
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
                var received = bitwarden.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "refused: " + request.Line));
                Assert.Equal("POST /identity/connect/token", received[0].Line); // the organization's key for a token, once - the rest with it
                Assert.Equal(new[]
                {
                    "GET /api/public/members",
                    "GET /api/public/members",
                    $"POST /api/public/groups {{\"name\":\"Fika & bröd\",\"accessAll\":false,\"externalId\":null,\"collections\":[{{\"id\":\"{CollectionId}\",\"readOnly\":true}}]}}",
                    $"GET /api/public/groups/{GroupId}",
                    $"PUT /api/public/groups/{GroupId} {{\"name\":\"Kassan\",\"accessAll\":false,\"externalId\":\"ext-7\",\"collections\":[{{\"id\":\"{CollectionId}\",\"readOnly\":true,\"hidePasswords\":true,\"manage\":false}}]}}",
                    $"PUT /api/public/groups/{GroupId}/member-ids {{\"memberIds\":[\"{MemberId}\"]}}",
                    "POST /api/public/members {\"type\":3,\"accessAll\":false,\"externalId\":null,\"collections\":[],\"email\":\"cy@kafe.example.test\"}",
                    "GET /api/public/events?start=2026-10-01T00%3A00%3A00Z",
                    "GET /api/public/events?start=2026-10-01T00%3A00%3A00Z&continuationToken=t1",
                }, received.Skip(1).Where(request => !request.Line.StartsWith("POST /identity", StringComparison.Ordinal)).Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(2, JsonNode.Parse(Outputs("members"))!["RecordCount"]!.GetValue<int>());
                Assert.Equal("Kassan", JsonNode.Parse(Outputs("rename"))!["Result"]![0]!["name"]!.GetValue<string>());
                Assert.True(JsonNode.Parse(Outputs("join"))!["Result"]![0]!["set"]!.GetValue<bool>());
                Assert.Equal(120, JsonNode.Parse(Outputs("events"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a self-hosted Bitwarden server: a token for the organization's client credentials alone, the API for that token alone.</summary>
        private sealed class BitwardenStandIn : IDisposable
        {
            private const string Token = "bw-drive-token";
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _clientId;
            private readonly string _secret;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public BitwardenStandIn(string clientId, string secret)
            {
                _clientId = clientId;
                _secret = secret;
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
                    var issuing = method == "POST" && path == "/identity/connect/token";
                    var authorized = issuing
                        ? body == $"grant_type=client_credentials&client_id={Uri.EscapeDataString(_clientId)}&client_secret={Uri.EscapeDataString(_secret)}&scope=api.organization"
                        : context.Request.Headers["Authorization"] == "Bearer " + Token;
                    lock (_gate)
                    {
                        // The token request's form carries the secret: logged by its line alone.
                        _received.Add(($"{method} {raw}{(issuing || body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    static string Events(int first, int count, string? next) => "{\"object\":\"list\",\"data\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"object\":\"event\",\"type\":1000,\"itemId\":null,\"date\":\"2026-10-0" + (1 + i % 9) + "T08:00:00Z\",\"actingUserId\":\"u" + i + "\"}")) + "],\"continuationToken\":" + (next is null ? "null" : "\"" + next + "\"") + "}";
                    const string Group = "{\"object\":\"group\",\"id\":\"" + GroupId + "\",\"name\":\"Kassa\",\"accessAll\":false,\"externalId\":\"ext-7\",\"collections\":[{\"id\":\"" + CollectionId + "\",\"readOnly\":true,\"hidePasswords\":true,\"manage\":false}]}";
                    var (status, answer) = !authorized ? (issuing ? 400 : 401, issuing ? "{\"error\":\"invalid_client\"}" : "{\"message\":\"Unauthorized.\"}")
                        : (method, path) switch
                        {
                            ("POST", "/identity/connect/token") => (200, "{\"access_token\":\"" + Token + "\",\"expires_in\":3600,\"token_type\":\"Bearer\"}"),
                            ("GET", "/api/public/members") => (200, "{\"object\":\"list\",\"data\":[{\"object\":\"member\",\"id\":\"" + MemberId + "\",\"email\":\"ada@kafe.example.test\",\"type\":2},{\"object\":\"member\",\"id\":\"m2\",\"email\":\"bo@kafe.example.test\",\"type\":1}],\"continuationToken\":null}"),
                            ("POST", "/api/public/groups") => (200, Group.Replace("Kassa", "Fika & bröd")),
                            ("GET", "/api/public/groups/" + GroupId) => (200, Group),
                            ("PUT", "/api/public/groups/" + GroupId) => (200, Group.Replace("Kassa", "Kassan")),
                            ("PUT", "/api/public/groups/" + GroupId + "/member-ids") => (200, ""),
                            ("POST", "/api/public/members") => (200, "{\"object\":\"member\",\"id\":\"m3\",\"email\":\"cy@kafe.example.test\",\"type\":3,\"status\":0}"),
                            ("GET", "/api/public/events") => (200, raw.Contains("continuationToken=t1", StringComparison.Ordinal) ? Events(101, 80, null) : Events(1, 100, "t1")),
                            _ => (404, "{\"message\":\"Resource not found.\"}"),
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
