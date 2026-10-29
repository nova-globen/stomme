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
    /// Rocket.Chat DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction
    /// port, against a loopback stand-in for a server's API that refuses a call without the user's id and token: a Rocket.Chat
    /// connection made over the API, its token vaulted, tested; a published pipeline posts a message with an accent and an
    /// attachment, reads the channel's messages over two pages - no more than asked - updates and deletes the message, reads
    /// the subscriptions and marks the room read; the token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class RocketChatDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_posts_reads_updates_and_deletes_messages_with_its_token()
        {
            var token = "rc" + Guid.NewGuid().ToString("N");
            using var rocketChat = new RocketChatStandIn("aobEdbYhXfu5hkeqG", token);
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
                    Name = "Shop chat", ConnectorType = "rocketchat", Endpoint = $"http://127.0.0.1:{rocketChat.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["username"] = "aobEdbYhXfu5hkeqG", ["password"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Chat" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("post", "Post", "post_message", "channel = #general\ntext = Kaffe på café\nattachments = [{\"title\": \"Fika\"}]"),
                        Call("read", "Read", "messages", "room = GENERAL\nmax = 120"),
                        Call("update", "Update", "update_message", "room = GENERAL\nmessage = m42\ntext = Kaffe!"),
                        Call("delete", "Delete", "delete_message", "room = GENERAL\nmessage = m42"),
                        Call("subscriptions", "Subscriptions", "subscriptions", ""),
                        Call("read-all", "Mark read", "mark_read", "room = GENERAL"),
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
                var received = rocketChat.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no id or token: " + request.Line));
                const string Newest = "sort=%7B%22ts%22%3A-1%7D";
                Assert.Equal(new[]
                {
                    "GET /api/v1/me",
                    "POST /api/v1/chat.postMessage {\"channel\":\"#general\",\"text\":\"Kaffe på café\",\"attachments\":[{\"title\":\"Fika\"}]}",
                    $"GET /api/v1/channels.messages?roomId=GENERAL&{Newest}&offset=0&count=100",
                    $"GET /api/v1/channels.messages?roomId=GENERAL&{Newest}&offset=100&count=100",
                    "POST /api/v1/chat.update {\"roomId\":\"GENERAL\",\"msgId\":\"m42\",\"text\":\"Kaffe!\"}",
                    "POST /api/v1/chat.delete {\"roomId\":\"GENERAL\",\"msgId\":\"m42\"}",
                    "GET /api/v1/subscriptions.get",
                    "POST /api/v1/subscriptions.read {\"rid\":\"GENERAL\"}",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("m42", JsonNode.Parse(Outputs("post"))!["Result"]![0]!["_id"]!.GetValue<string>());
                Assert.Equal(120, JsonNode.Parse(Outputs("read"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal("Kaffe!", JsonNode.Parse(Outputs("update"))!["Result"]![0]!["msg"]!.GetValue<string>());
                Assert.True(JsonNode.Parse(Outputs("delete"))!["Result"]![0]!["deleted"]!.GetValue<bool>());
                Assert.Equal(3, JsonNode.Parse(Outputs("subscriptions"))!["Result"]![0]!["unread"]!.GetValue<long>());
                Assert.True(JsonNode.Parse(Outputs("read-all"))!["Result"]![0]!["read"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a Rocket.Chat server's API that answers only a call carrying the user's id and token.</summary>
        private sealed class RocketChatStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _user;
            private readonly string _token;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public RocketChatStandIn(string user, string token)
            {
                _user = user;
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
                    var authorized = context.Request.Headers["X-User-Id"] == _user && context.Request.Headers["X-Auth-Token"] == _token;
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    static string Messages(int first, int count) => "{\"messages\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"_id\":\"m" + i + "\",\"msg\":\"Fika " + i + "\"}")) + "],\"total\":180,\"success\":true}";
                    var path = raw.Split('?')[0];
                    var (status, answer) = !authorized ? (401, "{\"status\":\"error\",\"message\":\"You must be logged in to do this.\"}")
                        : (method, path) switch
                        {
                            ("GET", "/api/v1/me") => (200, "{\"_id\":\"aobEdbYhXfu5hkeqG\",\"name\":\"Kafé bot\",\"username\":\"kafe-bot\",\"success\":true}"),
                            ("POST", "/api/v1/chat.postMessage") => (200, "{\"channel\":\"general\",\"message\":{\"_id\":\"m42\",\"msg\":\"Kaffe på café\"},\"success\":true}"),
                            ("GET", "/api/v1/channels.messages") => (200, raw.Contains("offset=100", StringComparison.Ordinal) ? Messages(101, 80) : Messages(1, 100)),
                            ("POST", "/api/v1/chat.update") => (200, "{\"message\":{\"_id\":\"m42\",\"msg\":\"Kaffe!\"},\"success\":true}"),
                            ("POST", "/api/v1/chat.delete") or ("POST", "/api/v1/subscriptions.read") => (200, "{\"success\":true}"),
                            ("GET", "/api/v1/subscriptions.get") => (200, "{\"update\":[{\"rid\":\"GENERAL\",\"name\":\"general\",\"unread\":3}],\"remove\":[],\"success\":true}"),
                            _ => (404, "{\"success\":false,\"error\":\"Not found\"}"),
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
