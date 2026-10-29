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
    /// Freshdesk DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for a helpdesk's API: a Freshdesk connection made over the API, its key vaulted, tested (the agent
    /// named); a published pipeline opens a ticket (open, low and portal by default), reads a requester's tickets over two pages,
    /// resolves one, adds a private note and replies - every call the key by Freshdesk's Basic pair; the key is in neither the graph
    /// nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class FreshdeskDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_opens_reads_resolves_notes_and_replies()
        {
            var key = "fd" + Guid.NewGuid().ToString("N");
            using var freshdesk = new FreshdeskStandIn();
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
                    Name = "Help desk", ConnectorType = "freshdesk", Endpoint = $"http://127.0.0.1:{freshdesk.Port}/a/tickets", TimeoutSeconds = 10, Credentials = new() { ["api-key"] = key },
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
                    Steps =
                    {
                        Call("open", "Open a ticket", "create_ticket", "email = ada@example.com\nsubject = Printer on fire\ndescription = <p>It broke.</p>\ntags = vip"),
                        Call("read", "Ada's tickets", "tickets", "requester_email = ada@example.com\nmax = 150"),
                        Call("resolve", "Resolve", "update_ticket", "ticket = 1001\nstatus = resolved"),
                        Call("note", "Note", "add_note", "ticket = 1001\nbody = Called."),
                        Call("reply", "Reply", "reply", "ticket = 1001\nbody = <p>Fixed.</p>"),
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
                var received = freshdesk.Received;
                var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(key + ":X"));
                Assert.All(received, request => Assert.Equal(basic, request.Authorization));
                Assert.Equal("/api/v2/agents/me", received[0].PathAndQuery); // the test, a page's address cut back to the helpdesk
                Assert.Equal("{\"email\":\"ada@example.com\",\"subject\":\"Printer on fire\",\"description\":\"<p>It broke.</p>\",\"status\":2,\"priority\":1,\"source\":2,\"tags\":[\"vip\"]}", received.Single(request => request.Method == "POST" && request.PathAndQuery == "/api/v2/tickets").Body);
                Assert.Equal(new[] { "/api/v2/tickets?email=ada%40example.com&per_page=100&page=1", "/api/v2/tickets?email=ada%40example.com&per_page=50&page=2" },
                    received.Where(request => request.Method == "GET" && request.PathAndQuery.StartsWith("/api/v2/tickets?", StringComparison.Ordinal)).Select(request => request.PathAndQuery));
                Assert.Equal("{\"status\":4}", received.Single(request => request.Method == "PUT").Body);
                Assert.Equal("{\"body\":\"Called.\",\"private\":true}", received.Single(request => request.PathAndQuery.EndsWith("/notes", StringComparison.Ordinal)).Body);
                Assert.Equal("{\"body\":\"<p>Fixed.</p>\"}", received.Single(request => request.PathAndQuery.EndsWith("/reply", StringComparison.Ordinal)).Body);

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                var read2 = JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == "read")!;
                Assert.Contains("\"RecordCount\": 150", read2["outputsJson"]!.GetValue<string>()); // both pages, up to the most asked for
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a helpdesk's API: the agent, a ticket opened, the tickets over pages, one resolved, a note and a reply.</summary>
        private sealed class FreshdeskStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string PathAndQuery, string? Authorization, string Body)> _received = new();

            public FreshdeskStandIn()
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

            private static string Tickets(int from, int count) =>
                "[" + string.Join(",", Enumerable.Range(from, count).Select(id => "{\"id\":" + id + ",\"subject\":\"Ticket " + id + "\",\"status\":2,\"priority\":1,\"source\":2}")) + "]";

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
                        ("GET", "/api/v2/agents/me") => (200, "{\"id\":8,\"contact\":{\"name\":\"Ada Lovelace\",\"email\":\"ada@acme.example\"}}"),
                        ("POST", "/api/v2/tickets") => (201, "{\"id\":1001,\"subject\":\"Printer on fire\",\"status\":2,\"priority\":1,\"source\":2}"),
                        ("GET", "/api/v2/tickets") when raw.EndsWith("page=2", StringComparison.Ordinal) => (200, Tickets(101, 100)),
                        ("GET", "/api/v2/tickets") => (200, Tickets(1, 100)),
                        ("PUT", "/api/v2/tickets/1001") => (200, "{\"id\":1001,\"status\":4}"),
                        ("POST", "/api/v2/tickets/1001/notes") => (201, "{\"id\":501,\"ticket_id\":1001,\"private\":true,\"body_text\":\"Called.\"}"),
                        ("POST", "/api/v2/tickets/1001/reply") => (201, "{\"id\":502,\"ticket_id\":1001,\"private\":false,\"body_text\":\"Fixed.\"}"),
                        _ => (404, "{\"code\":\"invalid_url\",\"message\":\"Invalid URL\"}"),
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
