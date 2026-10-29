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
    /// HaloPSA DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Halo's token endpoint and resource server: a token for the API application's client
    /// credentials alone, the resource server answering that token alone. A HaloPSA connection made over the API, its secret
    /// vaulted, tested; a published pipeline finds a client by Halo's search, creates a ticket for it, assigns it - the ticket
    /// got before it is written - reads the client's active tickets over two pages no further than asked and deletes one; the
    /// secret in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class HaloPsaDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string TokenPath = "/auth/token";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_keeps_a_desks_tickets_with_the_applications_credentials()
        {
            var secret = "halo-" + Guid.NewGuid().ToString("N");
            using var haloStandIn = new HaloStandIn("kafe-app-1", secret);
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
                    Name = "Kafé desk", ConnectorType = "halopsa", Endpoint = $"http://127.0.0.1:{haloStandIn.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["client-id"] = "kafe-app-1", ["client-secret"] = secret, ["token-url"] = $"http://127.0.0.1:{haloStandIn.Port}{TokenPath}" },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kafé desk" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("find", "Find", "records", "resource = clients\nsearch = Kafé"),
                        Call("new", "New", "create_record", "resource = tickets\nvalues = {\"summary\": \"Kassan är nere\", \"details\": \"Sedan nio\", \"tickettype_id\": 1, \"client_id\": 12}"),
                        Call("assign", "Assign", "update_record", "resource = tickets\nid = 1042\nvalues = {\"agent_id\": 4}"),
                        Call("open", "Open", "records", "resource = tickets\nstatus = active\nfilters = {\"client_id\": 12}\nmax = 150"),
                        Call("drop", "Drop", "delete_record", "resource = tickets\nid = 1"),
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
                var received = haloStandIn.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "refused: " + request.Line));
                Assert.Equal("POST " + TokenPath, received[0].Line); // the application's credentials for a token - the rest with it
                Assert.Equal(new[]
                {
                    "GET /api/Tickets?pageinate=true&page_size=1&page_no=1",
                    "GET /api/Client?pageinate=true&page_size=100&page_no=1&search=Kaf%C3%A9",
                    "POST /api/Tickets [{\"summary\":\"Kassan är nere\",\"details\":\"Sedan nio\",\"tickettype_id\":1,\"client_id\":12}]",
                    "GET /api/Tickets/1042",
                    "POST /api/Tickets [{\"id\":1042,\"agent_id\":4}]",
                    "GET /api/Tickets?pageinate=true&page_size=100&page_no=1&includeactive=true&includeinactive=false&client_id=12",
                    "GET /api/Tickets?pageinate=true&page_size=100&page_no=2&includeactive=true&includeinactive=false&client_id=12",
                    "DELETE /api/Tickets/1",
                }, received.Skip(1).Where(request => !request.Line.StartsWith("POST " + TokenPath, StringComparison.Ordinal)).Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("Kafé AB", JsonNode.Parse(Outputs("find"))!["Result"]![0]!["name"]!.GetValue<string>());
                Assert.Equal(1042, JsonNode.Parse(Outputs("new"))!["Result"]![0]!["id"]!.GetValue<long>());
                Assert.Equal(4, JsonNode.Parse(Outputs("assign"))!["Result"]![0]!["agent_id"]!.GetValue<long>());
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

        /// <summary>A loopback stand-in for Halo's token endpoint and resource server: a token for the application's client credentials alone, the resource server for that token alone.</summary>
        private sealed class HaloStandIn : IDisposable
        {
            private const string Token = "halo-drive-token";
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _form;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public HaloStandIn(string clientId, string secret)
            {
                // The form as a form is encoded: a space as +.
                static string Encoded(string value) => Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);
                _form = $"grant_type=client_credentials&client_id={Encoded(clientId)}&client_secret={Encoded(secret)}&scope={Encoded("admin edit:tickets edit:customers")}";
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

                    string Tickets(int first, int count) => "{\"record_count\":230,\"tickets\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":" + i + ",\"summary\":\"Kassan " + i + "\",\"client_id\":12}")) + "]}";
                    var (status, answer) = !authorized ? (401, issuing ? "{\"error\":\"invalid_client\"}" : "{\"message\":\"Authorization has been denied for this request.\"}")
                        : (method, path) switch
                        {
                            ("POST", TokenPath) => (200, "{\"access_token\":\"" + Token + "\",\"expires_in\":3600,\"token_type\":\"Bearer\"}"),
                            ("GET", "/api/Client") => (200, "{\"record_count\":1,\"clients\":[{\"id\":12,\"name\":\"Kafé AB\",\"is_vip\":true}]}"),
                            ("POST", "/api/Tickets") => (201, body.Contains("\"id\":1042", StringComparison.Ordinal) ? "{\"id\":1042,\"summary\":\"Kassan är nere\",\"agent_id\":4}" : "{\"id\":1042,\"summary\":\"Kassan är nere\",\"agent_id\":0}"),
                            ("GET", "/api/Tickets/1042") => (200, "{\"id\":1042,\"summary\":\"Kassan är nere\",\"agent_id\":0}"),
                            ("GET", "/api/Tickets") => (200, raw.Contains("page_no=2", StringComparison.Ordinal) ? Tickets(101, 100) : Tickets(1, raw.Contains("page_size=1&", StringComparison.Ordinal) ? 1 : 100)),
                            ("DELETE", "/api/Tickets/1") => (200, ""),
                            _ => (404, "\"The record could not be found\""),
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
