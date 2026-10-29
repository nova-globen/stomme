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
    /// Microsoft Entra ID DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the
    /// extraction port, against a loopback stand-in for the tenant's token endpoint and Microsoft Graph: a token for the app's
    /// client credentials alone, Graph answering that token alone. An Entra ID connection made over the API, its secret
    /// vaulted, tested; a published pipeline reads the users over two pages by Graph's next link no further than asked,
    /// updates a user - what Graph keeps apart in a request of its own - creates a group, adds the user to it and gets it with
    /// its members; the secret in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class EntraIdDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string TokenPath = "/kafe-tenant/oauth2/v2.0/token";
        private const string UserId = "87d349ed-44d7-43e1-9a83-5f2406dee5bd";
        private const string GroupId = "02bd9fd6-8f93-4758-87c3-1fb73740a315";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_keeps_users_and_groups_with_the_apps_credentials()
        {
            var secret = "entra-" + Guid.NewGuid().ToString("N");
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
                    Name = "Kafé directory", ConnectorType = "entra", Endpoint = $"http://127.0.0.1:{graphStandIn.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["client-id"] = "app-kafe-1", ["client-secret"] = secret, ["token-url"] = $"http://127.0.0.1:{graphStandIn.Port}{TokenPath}" },
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
                        Call("users", "Users", "users", "filter = accountEnabled eq true\nmax = 150\ntop = 100"),
                        Call("promote", "Promote", "update_user", $"user = {UserId}\nvalues = {{\"jobTitle\": \"Barista på Kafé\", \"aboutMe\": \"Fika\"}}"),
                        Call("team", "Team", "create_group", "display_name = Kassan\nmail_nickname = kassan\ndescription = Alla på kassan"),
                        Call("join", "Join", "add_to_group", $"user = ada@kafe.onmicrosoft.com\ngroup = {GroupId}"),
                        Call("group", "Group", "group", $"group = {GroupId}\ninclude_members = true"),
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
                var select = Uri.EscapeDataString("id,displayName,userPrincipalName,mail,accountEnabled,userType,createdDateTime");
                Assert.Equal(new[]
                {
                    "GET /v1.0/users?$top=1&$select=id",
                    $"GET /v1.0/users?$top=100&$select={select}&$filter=accountEnabled%20eq%20true",
                    $"GET /v1.0/users?$top=100&$skiptoken=p1",
                    $"PATCH /v1.0/users/{UserId} {{\"jobTitle\":\"Barista på Kafé\"}}",
                    $"PATCH /v1.0/users/{UserId} {{\"aboutMe\":\"Fika\"}}",
                    "POST /v1.0/groups {\"groupTypes\":[],\"mailEnabled\":false,\"securityEnabled\":true,\"displayName\":\"Kassan\",\"mailNickname\":\"kassan\",\"description\":\"Alla på kassan\"}",
                    $"POST /v1.0/groups/{GroupId}/members/$ref {{\"@odata.id\":\"http://127.0.0.1:{graphStandIn.Port}/v1.0/directoryObjects/ada%40kafe.onmicrosoft.com\"}}",
                    $"GET /v1.0/groups/{GroupId}?$select={Uri.EscapeDataString("id,displayName,description,mail,mailEnabled,mailNickname,securityEnabled,groupTypes,visibility,createdDateTime")}&$expand={Uri.EscapeDataString("members($select=id,displayName,userPrincipalName,mail)")}",
                }, received.Skip(1).Where(request => !request.Line.StartsWith("POST " + TokenPath, StringComparison.Ordinal)).Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(150, JsonNode.Parse(Outputs("users"))!["RecordCount"]!.GetValue<int>()); // both pages, no further than asked
                Assert.True(JsonNode.Parse(Outputs("promote"))!["Result"]![0]!["updated"]!.GetValue<bool>());
                Assert.Equal(GroupId, JsonNode.Parse(Outputs("team"))!["Result"]![0]!["id"]!.GetValue<string>());
                Assert.Contains("Ada Lovelace", JsonNode.Parse(Outputs("group"))!["Result"]![0]!["members"]!.GetValue<string>(), StringComparison.Ordinal);
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
            private const string Token = "entra-drive-token";
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
                    lock (_gate)
                    {
                        // The token request's form carries the secret: logged by its line alone.
                        _received.Add(($"{method} {raw}{(issuing || body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    var origin = $"http://127.0.0.1:{Port}/v1.0";
                    string Users(int first, int count, string? next) => "{\"value\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":\"" + Guid.Empty.ToString()[..^4] + i.ToString("D4") + "\",\"displayName\":\"User " + i + "\",\"accountEnabled\":true}")) + "]" + (next is null ? "" : ",\"@odata.nextLink\":\"" + next + "\"") + "}";
                    const string Group = "{\"@odata.context\":\"x\",\"id\":\"" + GroupId + "\",\"displayName\":\"Kassan\",\"securityEnabled\":true,\"groupTypes\":[]";
                    var (status, answer) = !authorized ? (401, issuing ? "{\"error\":\"invalid_client\"}" : "{\"error\":{\"code\":\"InvalidAuthenticationToken\",\"message\":\"Access token is empty.\"}}")
                        : (method, path) switch
                        {
                            ("POST", TokenPath) => (200, "{\"access_token\":\"" + Token + "\",\"expires_in\":3599,\"token_type\":\"Bearer\"}"),
                            ("GET", "/v1.0/users") => (200, raw.Contains("$top=1&", StringComparison.Ordinal) ? Users(1, 1, null) : raw.Contains("skiptoken=p1", StringComparison.Ordinal) ? Users(101, 100, null) : Users(1, 100, origin + "/users?$top=100&$skiptoken=p1")),
                            ("PATCH", "/v1.0/users/" + UserId) => (204, ""),
                            ("POST", "/v1.0/groups") => (201, Group + "}"),
                            ("POST", "/v1.0/groups/" + GroupId + "/members/$ref") => (204, ""),
                            ("GET", "/v1.0/groups/" + GroupId) => (200, Group + ",\"members\":[{\"id\":\"" + UserId + "\",\"displayName\":\"Ada Lovelace\"}]}"),
                            _ => (404, "{\"error\":{\"code\":\"Request_ResourceNotFound\",\"message\":\"Resource not found.\"}}"),
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
