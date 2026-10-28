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
    /// BL-229 - Zendesk DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for an account's Support API: a Zendesk connection made over the API, its API token vaulted,
    /// tested (the agent named); a published pipeline opens a ticket, reads the open tickets over two pages, answers one with a
    /// public reply and finds a requester - every call the agent's address with /token and the token; the token is in neither
    /// the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class ZendeskDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_opens_a_ticket_reads_open_tickets_answers_one_and_finds_a_requester()
        {
            var token = "zd" + Guid.NewGuid().ToString("N");
            using var zendesk = new ZendeskStandIn();
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
                    Name = "Help desk", ConnectorType = "zendesk", Endpoint = $"http://127.0.0.1:{zendesk.Port}", TimeoutSeconds = 10, Credentials = new() { ["username"] = "ada@acme.example", ["password"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Support" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-open", Name = "open" } },
                    Steps =
                    {
                        Call("open-one", "Open a ticket", "create_ticket", "subject = Printer on fire\ndescription = It broke.\nrequester_email = grace@example.com\npriority = high\ntags = vip"),
                        Call("read", "Open tickets", "tickets", "status = open"),
                        Call("answer", "Answer it", "update_ticket", "ticket = 5\ncomment = Fixed - try again.\nstatus = solved"),
                        Call("find", "Find Grace", "users", "query = grace@example.com"),
                    },
                };
                graph.Steps[1].OutputVariables["Result"] = "v-open";
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
                var received = zendesk.Received;
                var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("ada@acme.example/token:" + token));
                Assert.All(received, request => Assert.Equal(basic, request.Authorization));
                Assert.Equal("{\"ticket\":{\"subject\":\"Printer on fire\",\"comment\":{\"body\":\"It broke.\"},\"requester\":{\"email\":\"grace@example.com\"},\"priority\":\"high\",\"tags\":[\"vip\"]}}",
                    received.Single(request => request.Method == "POST" && request.PathAndQuery == "/api/v2/tickets.json").Body);
                var pages = received.Where(request => request.PathAndQuery.StartsWith("/api/v2/search.json?", StringComparison.Ordinal)).ToList();
                Assert.Equal(2, pages.Count); // both pages, by next_page
                Assert.Contains("query=type%3Aticket%20status%3Aopen", pages[0].PathAndQuery, StringComparison.Ordinal);
                Assert.Equal("{\"ticket\":{\"comment\":{\"body\":\"Fixed - try again.\",\"public\":true},\"status\":\"solved\"}}", received.Single(request => request.Method == "PUT").Body);
                Assert.Contains(received, request => request.PathAndQuery.StartsWith("/api/v2/users/search.json?query=grace%40example.com", StringComparison.Ordinal));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.Contains("Paper jam", steps); // the second page's ticket
                Assert.Contains("Grace Hopper", steps);
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a Zendesk account: the agent, a ticket opened and answered, the open tickets over two pages, a user found.</summary>
        private sealed class ZendeskStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string PathAndQuery, string? Authorization, string Body)> _received = new();

            public ZendeskStandIn()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Method, string PathAndQuery, string? Authorization, string Body)> Received
            {
                get
                {
                    lock (_gate)
                    {
                        return _received.ToArray();
                    }
                }
            }

            private string Ticket(int id, string subject, string status) =>
                "{\"id\":" + id + ",\"url\":\"http://127.0.0.1:" + Port + "/api/v2/tickets/" + id + ".json\",\"subject\":\"" + subject + "\",\"status\":\"" + status + "\",\"priority\":\"high\",\"tags\":[\"vip\"],\"result_type\":\"ticket\"}";

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

                    var raw = context.Request.RawUrl ?? "/";
                    var path = raw.Split('?')[0];
                    var method = context.Request.HttpMethod;
                    lock (_gate)
                    {
                        _received.Add((method, raw, context.Request.Headers["Authorization"], body));
                    }

                    var (status, answer) = (method, path) switch
                    {
                        ("GET", "/api/v2/users/me.json") => (200, "{\"user\":{\"id\":7,\"name\":\"Ada Lovelace\",\"role\":\"admin\"}}"),
                        ("POST", "/api/v2/tickets.json") => (201, "{\"ticket\":" + Ticket(5, "Printer on fire", "new") + "}"),
                        ("PUT", "/api/v2/tickets/5.json") => (200, "{\"ticket\":" + Ticket(5, "Printer on fire", "solved") + "}"),
                        ("GET", "/api/v2/search.json") when raw.Contains("page=2", StringComparison.Ordinal) => (200, "{\"results\":[" + Ticket(6, "Paper jam", "open") + "],\"next_page\":null}"),
                        ("GET", "/api/v2/search.json") => (200, "{\"results\":[" + Ticket(5, "Printer on fire", "open") + "],\"next_page\":\"http://127.0.0.1:" + Port + "/api/v2/search.json?page=2&per_page=100&query=type%3Aticket\"}"),
                        ("GET", "/api/v2/users/search.json") => (200, "{\"users\":[{\"id\":11,\"name\":\"Grace Hopper\",\"email\":\"grace@example.com\",\"role\":\"end-user\"}],\"next_page\":null}"),
                        _ => (404, "{\"error\":\"InvalidEndpoint\",\"description\":\"Not found\"}"),
                    };
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = status;
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
