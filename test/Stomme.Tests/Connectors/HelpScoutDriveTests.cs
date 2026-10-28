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
    /// BL-229 - Help Scout DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for Help Scout's token endpoint and Mailbox API: a token for the app's client
    /// credentials alone, the API answering that token alone. A Help Scout connection made over the API, its secret vaulted,
    /// tested; a published pipeline reads every conversation over two pages, creates one - got by the id it is answered with,
    /// not the address the answer names - adds a note to it, changes a customer's organization field by field and deletes the
    /// conversation; the secret in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class HelpScoutDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string TokenPath = "/v2/oauth2/token";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_keeps_an_inbox_with_the_apps_credentials()
        {
            var secret = "hs-" + Guid.NewGuid().ToString("N");
            using var helpScout = new HelpScoutStandIn("kafe-app-1", secret);
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
                    Name = "Kafé inbox", ConnectorType = "helpscout", Endpoint = $"http://127.0.0.1:{helpScout.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["client-id"] = "kafe-app-1", ["client-secret"] = secret, ["token-url"] = $"http://127.0.0.1:{helpScout.Port}{TokenPath}" },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé inbox" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("all", "All", "records", "resource = conversations\nfilters = {\"status\": \"all\"}"),
                        Call("new", "New", "create_record", "resource = conversations\nvalues = {\"subject\": \"Kassan är nere\", \"mailboxId\": 123, \"type\": \"email\", \"status\": \"active\", \"customer\": {\"email\": \"ada@kafe.example.com\"}, \"threads\": [{\"type\": \"customer\", \"customer\": {\"email\": \"ada@kafe.example.com\"}, \"text\": \"Hej\"}]}"),
                        Call("note", "Note", "create_record", "resource = threads\nconversation = 1043\nvalues = {\"type\": \"note\", \"text\": \"Ringde kunden\"}"),
                        Call("move", "Move", "update_record", "resource = customers\nid = 7\nvalues = {\"organization\": \"Kafé AB\"}"),
                        Call("drop", "Drop", "delete_record", "resource = conversations\nid = 1043"),
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
                var received = helpScout.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "refused: " + request.Line));
                Assert.Equal("POST " + TokenPath, received[0].Line); // the app's credentials for a token - the rest with it
                Assert.Equal(new[]
                {
                    "GET /v2/mailboxes",
                    "GET /v2/conversations?page=1&status=all",
                    "GET /v2/conversations?page=2&status=all",
                    "POST /v2/conversations {\"subject\":\"Kassan är nere\",\"mailboxId\":123,\"type\":\"email\",\"status\":\"active\",\"customer\":{\"email\":\"ada@kafe.example.com\"},\"threads\":[{\"type\":\"customer\",\"customer\":{\"email\":\"ada@kafe.example.com\"},\"text\":\"Hej\"}]}",
                    "GET /v2/conversations/1043",
                    "POST /v2/conversations/1043/notes {\"text\":\"Ringde kunden\"}",
                    "PATCH /v2/customers/7 [{\"op\":\"replace\",\"path\":\"/organization\",\"value\":\"Kafé AB\"}]",
                    "GET /v2/customers/7",
                    "DELETE /v2/conversations/1043",
                }, received.Skip(1).Where(request => !request.Line.StartsWith("POST " + TokenPath, StringComparison.Ordinal)).Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(70, JsonNode.Parse(Outputs("all"))!["RecordCount"]!.GetValue<int>()); // both pages
                Assert.Equal(1043, JsonNode.Parse(Outputs("new"))!["Result"]![0]!["id"]!.GetValue<long>());
                Assert.Equal(55, JsonNode.Parse(Outputs("note"))!["Result"]![0]!["id"]!.GetValue<long>());
                Assert.Equal("Kafé AB", JsonNode.Parse(Outputs("move"))!["Result"]![0]!["organization"]!.GetValue<string>());
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

        /// <summary>A loopback stand-in for Help Scout's token endpoint and Mailbox API: a token for the app's client credentials alone, the API for that token alone; a new record answered by its id and an address elsewhere.</summary>
        private sealed class HelpScoutStandIn : IDisposable
        {
            private const string Token = "hs-drive-token";
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _form;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public HelpScoutStandIn(string clientId, string secret)
            {
                static string Encoded(string value) => Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);
                _form = $"grant_type=client_credentials&client_id={Encoded(clientId)}&client_secret={Encoded(secret)}";
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
                    var authorized = issuing ? body == _form : context.Request.Headers["Authorization"] == "Bearer " + Token;
                    lock (_gate)
                    {
                        // The token request's form carries the secret: logged by its line alone.
                        _received.Add(($"{method} {raw}{(issuing || body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    string Conversations(int first, int count) => "{\"_embedded\":{\"conversations\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":" + i + ",\"subject\":\"Kassan " + i + "\",\"status\":\"active\"}")) + "]},\"page\":{\"size\":50,\"totalElements\":70,\"totalPages\":2,\"number\":" + (first == 1 ? 1 : 2) + "}}";
                    string? created = null;
                    var (status, answer) = !authorized ? (401, issuing ? "{\"error\":\"invalid_client\"}" : "{\"message\":\"Not authorized\"}")
                        : (method, path) switch
                        {
                            ("POST", TokenPath) => (200, "{\"access_token\":\"" + Token + "\",\"expires_in\":172800,\"token_type\":\"bearer\"}"),
                            ("GET", "/v2/mailboxes") => (200, "{\"_embedded\":{\"mailboxes\":[{\"id\":123,\"name\":\"Kafé\"}]},\"page\":{\"totalElements\":1,\"totalPages\":1,\"number\":1}}"),
                            ("GET", "/v2/conversations") => (200, raw.Contains("page=2", StringComparison.Ordinal) ? Conversations(51, 20) : Conversations(1, 50)),
                            ("POST", "/v2/conversations") => (201, ""),
                            ("GET", "/v2/conversations/1043") => (200, "{\"id\":1043,\"subject\":\"Kassan är nere\",\"status\":\"active\"}"),
                            ("POST", "/v2/conversations/1043/notes") => (201, ""),
                            ("PATCH", "/v2/customers/7") => (204, ""),
                            ("GET", "/v2/customers/7") => (200, "{\"id\":7,\"firstName\":\"Ada\",\"organization\":\"Kafé AB\"}"),
                            ("DELETE", "/v2/conversations/1043") => (204, ""),
                            _ => (404, "{\"message\":\"Resource not found\"}"),
                        };
                    if (status == 201)
                    {
                        created = path.EndsWith("/notes", StringComparison.Ordinal) ? "55" : "1043";
                        context.Response.AddHeader("Resource-ID", created);
                        context.Response.AddHeader("Location", "http://elsewhere.example.test/v2/conversations/" + created);
                    }

                    context.Response.StatusCode = status;
                    if (answer.Length > 0)
                    {
                        var bytes = Encoding.UTF8.GetBytes(answer);
                        context.Response.ContentType = "application/hal+json";
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
