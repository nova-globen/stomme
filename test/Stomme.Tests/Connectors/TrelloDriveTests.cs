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
    /// Trello DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Trello's API: a Trello connection made over the API, its token vaulted, tested (the member
    /// named); a published pipeline adds a card to a list, comments on it, moves it to Done marked complete and reads the list's
    /// cards - each call with the key and token in Trello's OAuth header and never in an address; the token is in neither the
    /// graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class TrelloDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string ToDo = "60a1b2c3d4e5f6a7b8c9d0e1";
        private const string Done = "60a1b2c3d4e5f6a7b8c9d0ff";
        private const string Card = "5f1a2b3c4d5e6f7a8b9c0d1e";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_adds_a_card_comments_on_it_moves_it_to_done_and_reads_the_list()
        {
            var token = "trello-" + Guid.NewGuid().ToString("N");
            using var trello = new TrelloStandIn();
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
                    Name = "Orders board", ConnectorType = "trello", Endpoint = $"http://127.0.0.1:{trello.Port}/1", TimeoutSeconds = 10,
                    Credentials = new() { ["access-key"] = "key-1", ["secret-key"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Order card" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-cards", Name = "cards" } },
                    Steps =
                    {
                        Call("add", "Add a card", "create_card", $"list = {ToDo}\nname = Order 1001\ndescription = Twelve, please.\ndue = 2026-10-12"),
                        Call("comment", "Say so", "comment", $"card = {Card}\ntext = Packed."),
                        Call("move", "Done", "update_card", $"card = {Card}\nlist = {Done}\ndue_complete = true"),
                        Call("cards", "To do", "cards", $"list = {ToDo}"),
                    },
                };
                graph.Steps[3].OutputVariables["Result"] = "v-cards";
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
                var received = trello.Received;
                Assert.All(received, request => Assert.Equal($"OAuth oauth_consumer_key=\"key-1\", oauth_token=\"{token}\"", request.Authorization));
                Assert.All(received, request => Assert.DoesNotContain(token, request.PathAndQuery, StringComparison.Ordinal)); // never in an address
                Assert.Equal("{\"name\":\"Order 1001\",\"desc\":\"Twelve, please.\",\"due\":\"2026-10-12T00:00:00.000Z\",\"idList\":\"" + ToDo + "\"}", received.Single(request => request.Method == "POST" && request.PathAndQuery == "/1/cards").Body);
                Assert.Equal("{\"text\":\"Packed.\"}", received.Single(request => request.PathAndQuery == $"/1/cards/{Card}/actions/comments").Body);
                Assert.Equal("{\"idList\":\"" + Done + "\",\"dueComplete\":true}", received.Single(request => request.Method == "PUT").Body);

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.Contains("Order 1007", steps); // the list's cards were the last step's result
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Trello's API: the member, a card added, its comment, its move, the list's cards.</summary>
        private sealed class TrelloStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string PathAndQuery, string? Authorization, string Body)> _received = new();

            public TrelloStandIn()
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

            private static string CardJson(string id, string name, string list) =>
                "{\"id\":\"" + id + "\",\"name\":\"" + name + "\",\"desc\":\"\",\"shortUrl\":\"https://trello.example/c/AbCd1234\",\"idList\":\"" + list + "\",\"idBoard\":\"b1\",\"due\":null,\"dueComplete\":false,\"closed\":false,\"labels\":[],\"idMembers\":[]}";

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

                    var pathAndQuery = context.Request.Url!.PathAndQuery;
                    var path = context.Request.Url.AbsolutePath;
                    var method = context.Request.HttpMethod;
                    lock (_gate)
                    {
                        _received.Add((method, pathAndQuery, context.Request.Headers["Authorization"], body));
                    }

                    var (status, answer) = (method, path) switch
                    {
                        ("GET", "/1/members/me") => (200, "{\"id\":\"m1\",\"fullName\":\"Ops Bot\"}"),
                        ("POST", "/1/cards") => (200, CardJson(Card, "Order 1001", ToDo)),
                        ("POST", _) when path == $"/1/cards/{Card}/actions/comments" => (200, "{\"id\":\"c777\"}"),
                        ("PUT", _) when path == $"/1/cards/{Card}" => (200, CardJson(Card, "Order 1001", Done)),
                        ("GET", _) when path == $"/1/lists/{ToDo}/cards" => (200, "[" + CardJson("5f1a2b3c4d5e6f7a8b9c0d77", "Order 1007", ToDo) + "]"),
                        _ => (404, "The requested resource was not found."),
                    };
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    context.Response.StatusCode = status;
                    context.Response.ContentType = answer.StartsWith('{') || answer.StartsWith('[') ? "application/json" : "text/plain";
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
