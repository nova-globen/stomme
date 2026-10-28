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
    /// BL-229 - Microsoft Graph Security DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the
    /// extraction port, against a loopback stand-in for the tenant's token endpoint and Microsoft Graph: the token endpoint
    /// issues a token for the app's client credentials alone, Graph answers that token alone. A Microsoft Graph Security
    /// connection made over the API, its secret vaulted, tested; a published pipeline reads the secure scores over two pages by
    /// Graph's next link with their control scores left out, gets one, reads the control profiles by a filter, gets one and
    /// updates it - Graph's vendor information, its state and a comment, Graph answering with the profile as it is now; the
    /// secret in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class MicrosoftGraphSecurityDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string TokenPath = "/kafe-tenant/oauth2/v2.0/token";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_reads_the_secure_scores_and_keeps_a_control_profile_with_the_apps_credentials()
        {
            var secret = "graph-" + Guid.NewGuid().ToString("N");
            using var graphStandIn = new GraphStandIn("app-kafe-1", secret);
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
                    Name = "Kafé security", ConnectorType = "microsoftgraphsecurity", Endpoint = $"http://127.0.0.1:{graphStandIn.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["client-id"] = "app-kafe-1", ["client-secret"] = secret, ["token-url"] = $"http://127.0.0.1:{graphStandIn.Port}{TokenPath}" },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé secure score" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("scores", "Scores", "secure_scores", "max = 150"),
                        Call("score", "Score", "secure_score", "id = kafe-tenant_2026-10-14"),
                        Call("profiles", "Profiles", "control_profiles", "filter = service eq 'Exchange'"),
                        Call("profile", "Profile", "control_profile", "id = AdminMFAV2"),
                        Call("ignore", "Ignore", "update_control_profile", "id = AdminMFAV2\nstate = Ignored\ncomment = Kafé har ett annat skydd"),
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
                var received = graphStandIn.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "refused: " + request.Line));
                Assert.Equal("POST " + TokenPath, received[0].Line); // the app's credentials for a token - the rest with it
                Assert.Equal(new[]
                {
                    "GET /v1.0/security/secureScores?$top=1",
                    "GET /v1.0/security/secureScores?$top=100",
                    "GET /v1.0/security/secureScores?$top=100&$skiptoken=p1",
                    "GET /v1.0/security/secureScores/kafe-tenant_2026-10-14",
                    "GET /v1.0/security/secureScoreControlProfiles?$top=100&$filter=service%20eq%20%27Exchange%27",
                    "GET /v1.0/security/secureScoreControlProfiles/AdminMFAV2",
                    "PATCH /v1.0/security/secureScoreControlProfiles/AdminMFAV2 return=representation {\"vendorInformation\":{\"provider\":\"SecureScore\",\"vendor\":\"Microsoft\"},\"state\":\"Ignored\",\"comment\":\"Kafé har ett annat skydd\"}",
                }, received.Skip(1).Where(request => !request.Line.StartsWith("POST " + TokenPath, StringComparison.Ordinal)).Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(150, JsonNode.Parse(Outputs("scores"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.DoesNotContain("controlScores", Outputs("scores"), StringComparison.Ordinal); // left out unless asked for
                Assert.Equal(48.5, JsonNode.Parse(Outputs("score"))!["Result"]![0]!["currentScore"]!.GetValue<double>());
                Assert.Equal("Ensure MFA for admins", JsonNode.Parse(Outputs("profile"))!["Result"]![0]!["title"]!.GetValue<string>());
                Assert.Contains("Ignored", JsonNode.Parse(Outputs("ignore"))!["Result"]![0]!["controlStateUpdates"]!.GetValue<string>(), StringComparison.Ordinal);
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a tenant's token endpoint and Microsoft Graph: a token for the app's client credentials alone, Graph for that token alone.</summary>
        private sealed class GraphStandIn : IDisposable
        {
            private const string Token = "graph-drive-token";
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _clientId;
            private readonly string _secret;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public GraphStandIn(string clientId, string secret)
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
                    var issuing = method == "POST" && path == TokenPath;
                    var authorized = issuing
                        ? body == $"grant_type=client_credentials&client_id={Uri.EscapeDataString(_clientId)}&client_secret={Uri.EscapeDataString(_secret)}&scope={Uri.EscapeDataString("https://graph.microsoft.com/.default")}"
                        : context.Request.Headers["Authorization"] == "Bearer " + Token;
                    var prefer = context.Request.Headers["Prefer"];
                    lock (_gate)
                    {
                        // The token request's form carries the secret: logged by its line alone.
                        _received.Add(($"{method} {raw}{(prefer is null ? "" : " " + prefer)}{(issuing || body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    var origin = $"http://127.0.0.1:{Port}/v1.0/security";
                    string Scores(int first, int count, string? next) => "{\"@odata.context\":\"" + origin + "/$metadata#security/secureScores\",\"value\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":\"kafe-tenant_2026-10-" + (i % 28 + 1).ToString("D2") + "\",\"currentScore\":" + (40 + i % 10) + ".5,\"maxScore\":120,\"controlScores\":[{\"controlName\":\"AdminMFAV2\",\"score\":10}]}")) + "]" + (next is null ? "" : ",\"@odata.nextLink\":\"" + next + "\"") + "}";
                    const string Profile = "{\"@odata.context\":\"x\",\"id\":\"AdminMFAV2\",\"title\":\"Ensure MFA for admins\",\"maxScore\":10,\"controlStateUpdates\":[{\"state\":\"Default\"}]}";
                    var (status, answer) = !authorized ? (401, issuing ?"{\"error\":\"invalid_client\"}" : "{\"error\":{\"code\":\"InvalidAuthenticationToken\",\"message\":\"Access token is empty.\"}}")
                        : (method, path) switch
                        {
                            ("POST", TokenPath) => (200, "{\"access_token\":\"" + Token + "\",\"expires_in\":3599,\"token_type\":\"Bearer\"}"),
                            ("GET", "/v1.0/security/secureScores") => (200, raw.Contains("$top=1", StringComparison.Ordinal) && !raw.Contains("$top=100", StringComparison.Ordinal) ? Scores(1, 1, null)
                                : raw.Contains("skiptoken=p1", StringComparison.Ordinal) ? Scores(101, 100, null)
                                : Scores(1, 100, origin + "/secureScores?$top=100&$skiptoken=p1")),
                            ("GET", "/v1.0/security/secureScores/kafe-tenant_2026-10-14") => (200, "{\"@odata.context\":\"x\",\"id\":\"kafe-tenant_2026-10-14\",\"currentScore\":48.5,\"maxScore\":120}"),
                            ("GET", "/v1.0/security/secureScoreControlProfiles") => (200, "{\"value\":[" + Profile + "]}"),
                            ("GET", "/v1.0/security/secureScoreControlProfiles/AdminMFAV2") => (200, Profile),
                            ("PATCH", "/v1.0/security/secureScoreControlProfiles/AdminMFAV2") => (200, Profile.Replace("Default", "Ignored")),
                            _ => (404, "{\"error\":{\"code\":\"ResourceNotFound\",\"message\":\"Resource not found.\"}}"),
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
