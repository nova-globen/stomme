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
    /// BL-229 - Zulip DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for an organization's API that refuses a call without the bot's e-mail and key: a Zulip
    /// connection made over the API, its key vaulted, tested; a published pipeline sends to a stream's topic with an accent,
    /// reads the topic's messages over two pages - no more than asked - gets, updates and deletes the message, and reads the
    /// streams and the users; the key in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class ZulipDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_sends_reads_gets_updates_and_deletes_messages_and_reads_streams_and_users_with_its_key()
        {
            var key = "zk" + Guid.NewGuid().ToString("N");
            using var zulip = new ZulipStandIn("kafe-bot@chat.example.test:" + key);
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
                    Name = "Shop chat", ConnectorType = "zulip", Endpoint = $"http://127.0.0.1:{zulip.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["username"] = "kafe-bot@chat.example.test", ["password"] = key },
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
                        Call("send", "Send", "send_stream", "stream = kafé\ntopic = Fika\ncontent = Kaffe på café"),
                        Call("read", "Read", "messages", "stream = kafé\ntopic = Fika\nmax = 120"),
                        Call("get", "Get", "message", "id = 42"),
                        Call("update", "Update", "update_message", "id = 42\ncontent = Kaffe!"),
                        Call("delete", "Delete", "delete_message", "id = 42"),
                        Call("streams", "Streams", "streams", ""),
                        Call("users", "Users", "users", ""),
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
                var received = zulip.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no key: " + request.Line));
                const string Narrow = "narrow=%5B%7B%22operator%22%3A%22stream%22%2C%22operand%22%3A%22kaf%C3%A9%22%7D%2C%7B%22operator%22%3A%22topic%22%2C%22operand%22%3A%22Fika%22%7D%5D";
                Assert.Equal(new[]
                {
                    "GET /api/v1/users/me",
                    "POST /api/v1/messages type=stream&to=kaf%C3%A9&topic=Fika&content=Kaffe+p%C3%A5+caf%C3%A9",
                    $"GET /api/v1/messages?anchor=newest&include_anchor=true&num_before=100&num_after=0&apply_markdown=false&{Narrow}",
                    $"GET /api/v1/messages?anchor=401&include_anchor=false&num_before=100&num_after=0&apply_markdown=false&{Narrow}",
                    "GET /api/v1/messages/42",
                    "PATCH /api/v1/messages/42 content=Kaffe%21",
                    "DELETE /api/v1/messages/42",
                    "GET /api/v1/streams",
                    "GET /api/v1/users",
                }, received.Select(request => request.Line));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal(42, JsonNode.Parse(Outputs("send"))!["Result"]![0]!["id"]!.GetValue<long>());
                Assert.Equal(120, JsonNode.Parse(Outputs("read"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal("Fika", JsonNode.Parse(Outputs("get"))!["Result"]![0]!["topic"]!.GetValue<string>());
                Assert.Equal("kafé", JsonNode.Parse(Outputs("streams"))!["Result"]![0]!["name"]!.GetValue<string>());
                Assert.Equal(2, JsonNode.Parse(Outputs("users"))!["RecordCount"]!.GetValue<int>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(key, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(key, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for an organization's Zulip API that answers only a call carrying the bot's e-mail and key.</summary>
        private sealed class ZulipStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _basic;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public ZulipStandIn(string emailAndKey)
            {
                _basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(emailAndKey));
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
                    var authorized = context.Request.Headers["Authorization"] == _basic;
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    string Messages(int newest, int count, bool oldest) => "{\"result\":\"success\",\"found_oldest\":" + (oldest ? "true" : "false") + ",\"messages\":[" + string.Join(",", Enumerable.Range(newest - count + 1, count).Select(i => "{\"id\":" + i + ",\"subject\":\"Fika\",\"content\":\"Kaffe " + i + "\"}")) + "]}";
                    var path = raw.Split('?')[0];
                    var (status, answer) = !authorized ? (401, "{\"result\":\"error\",\"msg\":\"Invalid API key\",\"code\":\"UNAUTHORIZED\"}")
                        : (method, path) switch
                        {
                            ("GET", "/api/v1/users/me") => (200, "{\"result\":\"success\",\"full_name\":\"Kafé bot\",\"email\":\"kafe-bot@chat.example.test\"}"),
                            ("POST", "/api/v1/messages") => (200, "{\"result\":\"success\",\"msg\":\"\",\"id\":42}"),
                            ("GET", "/api/v1/messages") => (200, raw.Contains("anchor=newest", StringComparison.Ordinal) ? Messages(500, 100, false) : Messages(400, 80, false)),
                            ("GET", "/api/v1/messages/42") => (200, "{\"result\":\"success\",\"message\":{\"id\":42,\"subject\":\"Fika\",\"content\":\"Kaffe\"},\"raw_content\":\"Kaffe\"}"),
                            ("PATCH", "/api/v1/messages/42") or ("DELETE", "/api/v1/messages/42") => (200, "{\"result\":\"success\",\"msg\":\"\"}"),
                            ("GET", "/api/v1/streams") => (200, "{\"result\":\"success\",\"streams\":[{\"stream_id\":7,\"name\":\"kafé\"}]}"),
                            ("GET", "/api/v1/users") => (200, "{\"result\":\"success\",\"members\":[{\"user_id\":9,\"full_name\":\"Ada\"},{\"user_id\":12,\"full_name\":\"Kafé bot\"}]}"),
                            _ => (404, "{\"result\":\"error\",\"msg\":\"Not found\",\"code\":\"BAD_REQUEST\"}"),
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
