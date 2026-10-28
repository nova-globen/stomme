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
    /// BL-229 - Zammad DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for a help desk that refuses a call without the token: a Zammad connection made over the
    /// API, its token vaulted, tested; a published pipeline creates a ticket with an accent and a custom field, updates it with
    /// an internal note, gets it with its articles, reads the tickets over two pages - no more than asked - and creates a user;
    /// the token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class ZammadDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_updates_reads_tickets_and_creates_a_user_with_its_token()
        {
            var token = "zt" + Guid.NewGuid().ToString("N");
            using var desk = new DeskStandIn(token);
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
                    Name = "Shop desk", ConnectorType = "zammad", Endpoint = $"http://127.0.0.1:{desk.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["api-key"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Fika desk" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("create", "Create", "create_ticket", "title = Fika at 3\ngroup = Users\ncustomer = ada@kafe.example.test\nbody = Who is in? Kafé!\nfields = {\"order_no\": \"A-17\"}"),
                        Call("update", "Update", "update_ticket", "id = 7\nstate = open\nnote = Moved to 4"),
                        Call("get", "Get", "ticket", "id = 7"),
                        Call("read", "Read", "tickets", "max = 120"),
                        Call("user", "User", "create_user", "firstname = Bo\nlastname = Bröd\nemail = bo@kafe.example.test"),
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
                var received = desk.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token: " + request.Line));
                Assert.Equal(new[]
                {
                    "GET /api/v1/users/me",
                    "POST /api/v1/tickets {\"title\":\"Fika at 3\",\"group\":\"Users\",\"customer\":\"ada@kafe.example.test\",\"order_no\":\"A-17\",\"article\":{\"body\":\"Who is in? Kafé!\",\"type\":\"note\",\"internal\":true,\"sender\":\"Agent\"}}",
                    "PUT /api/v1/tickets/7 {\"state\":\"open\",\"article\":{\"body\":\"Moved to 4\",\"type\":\"note\",\"internal\":true,\"content_type\":\"text/html\"}}",
                    "GET /api/v1/tickets/7",
                    "GET /api/v1/ticket_articles/by_ticket/7",
                    "GET /api/v1/tickets?page=1&per_page=100",
                    "GET /api/v1/tickets?page=2&per_page=100",
                    "POST /api/v1/users {\"firstname\":\"Bo\",\"lastname\":\"Bröd\",\"email\":\"bo@kafe.example.test\"}",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("31007", JsonNode.Parse(Outputs("create"))!["Result"]![0]!["number"]!.GetValue<string>());
                Assert.Equal(2, JsonNode.Parse(Outputs("update"))!["Result"]![0]!["state_id"]!.GetValue<long>());
                Assert.Contains("Moved to 4", JsonNode.Parse(Outputs("get"))!["Result"]![0]!["articles"]!.GetValue<string>(), StringComparison.Ordinal);
                Assert.Equal(120, JsonNode.Parse(Outputs("read"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal(12, JsonNode.Parse(Outputs("user"))!["Result"]![0]!["id"]!.GetValue<long>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a Zammad help desk that answers only a call carrying the token.</summary>
        private sealed class DeskStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _token;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public DeskStandIn(string token)
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
                    var authorized = context.Request.Headers["Authorization"] == "Token token=" + _token;
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    static string Tickets(int first, int count) => "[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"id\":" + i + ",\"title\":\"Fika " + i + "\"}")) + "]";
                    var path = raw.Split('?')[0];
                    var (status, answer) = !authorized ? (401, "{\"error\":\"Invalid token!\"}")
                        : (method, path) switch
                        {
                            ("GET", "/api/v1/users/me") => (200, "{\"id\":3,\"firstname\":\"Ada\",\"lastname\":\"Kafé\",\"email\":\"ada@kafe.example.test\"}"),
                            ("POST", "/api/v1/tickets") => (201, "{\"id\":7,\"number\":\"31007\",\"title\":\"Fika at 3\",\"state_id\":1}"),
                            ("PUT", "/api/v1/tickets/7") => (200, "{\"id\":7,\"number\":\"31007\",\"state_id\":2}"),
                            ("GET", "/api/v1/tickets/7") => (200, "{\"id\":7,\"number\":\"31007\",\"title\":\"Fika at 3\",\"state_id\":2}"),
                            ("GET", "/api/v1/ticket_articles/by_ticket/7") => (200, "[{\"id\":9,\"body\":\"Who is in? Kafé!\"},{\"id\":10,\"body\":\"Moved to 4\",\"internal\":true}]"),
                            ("GET", "/api/v1/tickets") => (200, raw.Contains("page=2", StringComparison.Ordinal) ? Tickets(101, 80) : Tickets(1, 100)),
                            ("POST", "/api/v1/users") => (201, "{\"id\":12,\"firstname\":\"Bo\",\"lastname\":\"Bröd\"}"),
                            _ => (404, "{\"error\":\"No route matches\"}"),
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
