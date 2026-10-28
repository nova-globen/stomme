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
    /// BL-229 - Microsoft Dynamics 365 DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the
    /// extraction port, against a loopback stand-in for the tenant's token endpoint and an environment's Dataverse Web API: a
    /// token for the application user's client credentials and the environment's own scope alone, the environment answering
    /// that token alone. A Microsoft Dynamics 365 connection made over the API, its secret vaulted, tested; a published pipeline
    /// creates an account bound to its primary contact, updates it - never creating one - reads the active accounts over two
    /// pages by the environment's next link no further than asked, gets one with its contact and deletes it; the secret in
    /// neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class MicrosoftDynamicsDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string TokenPath = "/kafe-tenant/oauth2/v2.0/token";
        private const string AccountId = "475b158c-541c-e511-80d3-3863bb347ba8";
        private const string ContactId = "00000000-0000-0000-0000-000000000001";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_keeps_accounts_with_the_application_users_credentials()
        {
            var secret = "crm-" + Guid.NewGuid().ToString("N");
            using var environment = new DataverseStandIn("app-kafe-1", secret);
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
                    Name = "Kafé CRM", ConnectorType = "microsoftdynamics", Endpoint = $"http://127.0.0.1:{environment.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["client-id"] = "app-kafe-1", ["client-secret"] = secret, ["token-url"] = $"http://127.0.0.1:{environment.Port}{TokenPath}" },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé accounts" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("open", "Open", "create_record", $"table = accounts\nselect = accountid,name\nvalues = {{\"name\": \"Kafé AB\", \"revenue\": 1200.5, \"primarycontactid@odata.bind\": \"/contacts({ContactId})\"}}"),
                        Call("phone", "Phone", "update_record", $"table = accounts\nid = {AccountId}\nvalues = {{\"telephone1\": \"+46 8 123\"}}"),
                        Call("active", "Active", "records", "table = accounts\nfilter = statecode eq 0\nselect = name\nmax = 150"),
                        Call("one", "One", "record", $"table = accounts\nid = {AccountId}\nexpand = primarycontactid($select=fullname)"),
                        Call("close", "Close", "delete_record", $"table = accounts\nid = {AccountId}"),
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
                var received = environment.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "refused: " + request.Line));
                Assert.Equal("POST " + TokenPath, received[0].Line); // the application user's credentials for a token - the rest with it
                Assert.Equal(new[]
                {
                    "GET /api/data/v9.2/WhoAmI",
                    $"POST /api/data/v9.2/accounts?$select=accountid,name prefer=return=representation {{\"name\":\"Kafé AB\",\"revenue\":1200.5,\"primarycontactid@odata.bind\":\"/contacts({ContactId})\"}}",
                    $"PATCH /api/data/v9.2/accounts({AccountId}) prefer=return=representation if-match=* {{\"telephone1\":\"+46 8 123\"}}",
                    "GET /api/data/v9.2/accounts?$select=name&$filter=statecode%20eq%200 prefer=odata.maxpagesize=150",
                    "GET /api/data/v9.2/accounts?$select=name&$skiptoken=p1 prefer=odata.maxpagesize=150",
                    $"GET /api/data/v9.2/accounts({AccountId})?$expand=primarycontactid%28%24select%3Dfullname%29",
                    $"DELETE /api/data/v9.2/accounts({AccountId})",
                }, received.Skip(1).Where(request => !request.Line.StartsWith("POST " + TokenPath, StringComparison.Ordinal)).Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(AccountId, JsonNode.Parse(Outputs("open"))!["Result"]![0]!["accountid"]!.GetValue<string>());
                Assert.Equal("+46 8 123", JsonNode.Parse(Outputs("phone"))!["Result"]![0]!["telephone1"]!.GetValue<string>());
                Assert.Equal(150, JsonNode.Parse(Outputs("active"))!["RecordCount"]!.GetValue<int>()); // both pages, no further than asked
                Assert.Contains("Ada Lovelace", JsonNode.Parse(Outputs("one"))!["Result"]![0]!["primarycontactid"]!.GetValue<string>(), StringComparison.Ordinal);
                Assert.True(JsonNode.Parse(Outputs("close"))!["Result"]![0]!["deleted"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a tenant's token endpoint and an environment's Dataverse Web API: a token for the application user's credentials and the environment's scope alone, the API for that token alone.</summary>
        private sealed class DataverseStandIn : IDisposable
        {
            private const string Token = "crm-drive-token";
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _clientId;
            private readonly string _secret;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public DataverseStandIn(string clientId, string secret)
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
                        ? body == $"grant_type=client_credentials&client_id={Uri.EscapeDataString(_clientId)}&client_secret={Uri.EscapeDataString(_secret)}&scope={Uri.EscapeDataString($"http://127.0.0.1:{Port}/.default")}"
                        : context.Request.Headers["Authorization"] == "Bearer " + Token && context.Request.Headers["OData-Version"] == "4.0";
                    var prefer = context.Request.Headers["Prefer"];
                    var match = context.Request.Headers["If-Match"];
                    lock (_gate)
                    {
                        // The token request's form carries the secret: logged by its line alone.
                        _received.Add(($"{method} {raw}{(prefer is null ? "" : " prefer=" + prefer)}{(match is null ? "" : " if-match=" + match)}{(issuing || body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    var api = $"http://127.0.0.1:{Port}/api/data/v9.2";
                    string Accounts(int first, int count, string? next) => "{\"value\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"@odata.etag\":\"W/\\\"" + i + "\\\"\",\"accountid\":\"" + Guid.Empty.ToString()[..^4] + i.ToString("D4") + "\",\"name\":\"Kafé " + i + "\"}")) + "]" + (next is null ? "" : ",\"@odata.nextLink\":\"" + next + "\"") + "}";
                    const string Account = "{\"@odata.etag\":\"W/\\\"7\\\"\",\"accountid\":\"" + AccountId + "\",\"name\":\"Kafé AB\"";
                    var (status, answer) = !authorized ? (401, issuing ? "{\"error\":\"invalid_client\"}" : "{\"error\":{\"code\":\"0x80072560\",\"message\":\"The user is not a member of the organization.\"}}")
                        : (method, path) switch
                        {
                            ("POST", TokenPath) => (200, "{\"access_token\":\"" + Token + "\",\"expires_in\":3599,\"token_type\":\"Bearer\"}"),
                            ("GET", "/api/data/v9.2/WhoAmI") => (200, "{\"BusinessUnitId\":\"b1\",\"UserId\":\"a8c1f0b4-1111-4a2b-9c3d-000000000042\",\"OrganizationId\":\"o1\"}"),
                            ("POST", "/api/data/v9.2/accounts") => (201, Account + "}"),
                            ("PATCH", "/api/data/v9.2/accounts(" + AccountId + ")") => (200, Account + ",\"telephone1\":\"+46 8 123\"}"),
                            ("GET", "/api/data/v9.2/accounts") => (200, raw.Contains("skiptoken=p1", StringComparison.Ordinal) ? Accounts(101, 100, null) : Accounts(1, 100, api + "/accounts?$select=name&$skiptoken=p1")),
                            ("GET", "/api/data/v9.2/accounts(" + AccountId + ")") => (200, Account + ",\"primarycontactid\":{\"fullname\":\"Ada Lovelace\"}}"),
                            ("DELETE", "/api/data/v9.2/accounts(" + AccountId + ")") => (204, ""),
                            _ => (404, "{\"error\":{\"code\":\"0x80060888\",\"message\":\"Resource not found for the segment.\"}}"),
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
