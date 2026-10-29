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
    /// Microsoft SharePoint DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the
    /// extraction port, against a loopback stand-in for the tenant's token endpoint and Graph's sites API: a token for the app's
    /// client credentials alone, Graph answering that token alone. A Microsoft SharePoint connection made over the API, its secret
    /// vaulted, tested; a published pipeline reads a site's lists, creates an item, updates it, creates or updates one by its
    /// title, reads the open items by a filter over two pages no further than asked and deletes the first; the secret in neither
    /// the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class MicrosoftSharePointDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string TokenPath = "/kafe-tenant/oauth2/v2.0/token";
        private const string Items = "/v1.0/sites/kafe.sharepoint.com:/sites/Kassa:/lists/Orders/items";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_keeps_list_items_with_the_apps_credentials()
        {
            var secret = "sp-" + Guid.NewGuid().ToString("N");
            using var graphStandIn = new SharePointStandIn("app-kafe-1", secret);
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
                    Name = "Kafé intranet", ConnectorType = "microsoftsharepoint", Endpoint = $"http://127.0.0.1:{graphStandIn.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["site"] = "kafe.sharepoint.com:/sites/Kassa" },
                    Credentials = new() { ["client-id"] = "app-kafe-1", ["client-secret"] = secret, ["token-url"] = $"http://127.0.0.1:{graphStandIn.Port}{TokenPath}" },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé orders" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("lists", "Lists", "lists", ""),
                        Call("new", "New", "create_item", "list = Orders\nvalues = {\"Title\": \"Order 8\", \"Status\": \"Öppen\", \"Total\": 42}"),
                        Call("pay", "Pay", "update_item", "list = Orders\nid = 8\nvalues = {\"Status\": \"Betald\"}"),
                        Call("keep", "Keep", "upsert_item", "list = Orders\nmatch = Title\nvalues = {\"Title\": \"Order 9\", \"Status\": \"Öppen\"}"),
                        Call("open", "Open", "items", "list = Orders\nfilter = fields/Status eq 'Öppen'\nmax = 150"),
                        Call("drop", "Drop", "delete_item", "list = Orders\nid = 1"),
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
                    "GET /v1.0/sites/kafe.sharepoint.com:/sites/Kassa:?$select=displayName,webUrl",
                    "GET /v1.0/sites/kafe.sharepoint.com:/sites/Kassa:/lists?$select=id,name,displayName,description,webUrl,list,createdDateTime,lastModifiedDateTime",
                    $"POST {Items} {{\"fields\":{{\"Title\":\"Order 8\",\"Status\":\"Öppen\",\"Total\":42}}}}",
                    $"PATCH {Items}/8/fields {{\"Status\":\"Betald\"}}",
                    $"GET {Items}?$expand=fields&$top=2&$filter=fields%2FTitle%20eq%20%27Order%209%27 prefer=HonorNonIndexedQueriesWarningMayFailRandomly",
                    $"POST {Items} {{\"fields\":{{\"Title\":\"Order 9\",\"Status\":\"Öppen\"}}}}",
                    $"GET {Items}?$expand=fields&$top=150&$filter=fields%2FStatus%20eq%20%27%C3%96ppen%27 prefer=HonorNonIndexedQueriesWarningMayFailRandomly",
                    $"GET {Items}?$expand=fields&$skiptoken=p1 prefer=HonorNonIndexedQueriesWarningMayFailRandomly",
                    $"DELETE {Items}/1",
                }, received.Skip(1).Where(request => !request.Line.StartsWith("POST " + TokenPath, StringComparison.Ordinal)).Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("Orders", JsonNode.Parse(Outputs("lists"))!["Result"]![0]!["displayName"]!.GetValue<string>());
                Assert.Equal("8", JsonNode.Parse(Outputs("new"))!["Result"]![0]!["id"]!.GetValue<string>());
                Assert.Equal("Betald", JsonNode.Parse(Outputs("pay"))!["Result"]![0]!["Status"]!.GetValue<string>());
                Assert.True(JsonNode.Parse(Outputs("keep"))!["Result"]![0]!["created"]!.GetValue<bool>());
                Assert.Equal(150, JsonNode.Parse(Outputs("open"))!["RecordCount"]!.GetValue<int>()); // both pages, no further than asked
                Assert.True(JsonNode.Parse(Outputs("drop"))!["Result"]![0]!["deleted"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a tenant's token endpoint and Graph's sites API: a token for the app's client credentials alone, Graph for that token alone.</summary>
        private sealed class SharePointStandIn : IDisposable
        {
            private const string Token = "sp-drive-token";
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _clientId;
            private readonly string _secret;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public SharePointStandIn(string clientId, string secret)
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
                        _received.Add(($"{method} {raw}{(issuing || body.Length == 0 ? "" : " " + body)}{(prefer is null ? "" : " prefer=" + prefer)}", authorized));
                    }

                    var origin = $"http://127.0.0.1:{Port}";
                    string Page(int first, int count, string? next) => "{\"value\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":\"" + i + "\",\"webUrl\":\"https://kafe.sharepoint.com/sites/Kassa/Lists/Orders/" + i + "_.000\",\"fields\":{\"Title\":\"Order " + i + "\",\"Status\":\"Öppen\"}}")) + "]" + (next is null ? "" : ",\"@odata.nextLink\":\"" + next + "\"") + "}";
                    var (status, answer) = !authorized ? (401, issuing ? "{\"error\":\"invalid_client\"}" : "{\"error\":{\"code\":\"InvalidAuthenticationToken\",\"message\":\"Access token is empty.\"}}")
                        : (method, path) switch
                        {
                            ("POST", TokenPath) => (200, "{\"access_token\":\"" + Token + "\",\"expires_in\":3599,\"token_type\":\"Bearer\"}"),
                            ("GET", "/v1.0/sites/kafe.sharepoint.com:/sites/Kassa:") => (200, "{\"displayName\":\"Kassa\",\"webUrl\":\"https://kafe.sharepoint.com/sites/Kassa\"}"),
                            ("GET", "/v1.0/sites/kafe.sharepoint.com:/sites/Kassa:/lists") => (200, "{\"value\":[{\"id\":\"5b6c1e2a\",\"name\":\"Orders\",\"displayName\":\"Orders\"}]}"),
                            ("POST", Items) => (201, body.Contains("Order 9", StringComparison.Ordinal) ? "{\"id\":\"9\",\"fields\":{\"Title\":\"Order 9\",\"Status\":\"Öppen\"}}" : "{\"id\":\"8\",\"fields\":{\"Title\":\"Order 8\",\"Status\":\"Öppen\",\"Total\":42}}"),
                            ("PATCH", Items + "/8/fields") => (200, "{\"Title\":\"Order 8\",\"Status\":\"Betald\",\"Total\":42}"),
                            ("GET", Items) => (200, raw.Contains("Title", StringComparison.Ordinal) ? "{\"value\":[]}" : raw.Contains("skiptoken=p1", StringComparison.Ordinal) ? Page(101, 100, null) : Page(1, 100, origin + Items + "?$expand=fields&$skiptoken=p1")),
                            ("DELETE", Items + "/1") => (204, ""),
                            _ => (404, "{\"error\":{\"code\":\"itemNotFound\",\"message\":\"The resource could not be found.\"}}"),
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
