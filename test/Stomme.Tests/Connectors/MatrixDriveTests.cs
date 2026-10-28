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
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Models.Pipelines;
using Stomme.Tests.Pipelines;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Connectors
{
    /// <summary>
    /// BL-229 - Matrix DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for a homeserver that refuses a call without the token: a Matrix connection made over the
    /// API, its token vaulted, tested; a published pipeline creates a room with an alias and an invitee, sends a message with
    /// an accent and its HTML, invites someone, reads the room's messages over two pages by the homeserver's token - no more
    /// than asked - and its members, kicks the invitee and leaves; the token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class MatrixDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_creates_a_room_talks_reads_and_leaves_with_its_token()
        {
            var token = "syt_" + Guid.NewGuid().ToString("N");
            using var homeserver = new HomeserverStandIn(token);
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
                    Name = "Shop chat", ConnectorType = "matrix", Endpoint = $"http://127.0.0.1:{homeserver.Port}", TimeoutSeconds = 10,
                    Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Fika room" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string name, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = name, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        Call("room", "Room", "create_room", "name = Fika & bröd\nalias = fika\ninvite = @bo:kafe.example.test"),
                        Call("send", "Send", "send_message", "room = !fika:kafe.example.test\ntext = Fika at 3 - Kafé\nhtml = <b>Fika</b> at 3"),
                        Call("invite", "Invite", "invite", "room = !fika:kafe.example.test\nuser = @cy:kafe.example.test"),
                        Call("read", "Read", "messages", "room = !fika:kafe.example.test\nmax = 120"),
                        Call("members", "Members", "members", "room = !fika:kafe.example.test\nmembership = join"),
                        Call("kick", "Kick", "kick", "room = !fika:kafe.example.test\nuser = @cy:kafe.example.test\nreason = Wrong room"),
                        Call("leave", "Leave", "leave_room", "room = !fika:kafe.example.test"),
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
                var received = homeserver.Received;
                Assert.All(received, request => Assert.True(request.Authorized, "no token: " + request.Line));
                const string Room = "/_matrix/client/v3/rooms/%21fika%3Akafe.example.test";
                Assert.Equal(new[]
                {
                    "GET /_matrix/client/v3/account/whoami",
                    "POST /_matrix/client/v3/createRoom {\"name\":\"Fika & bröd\",\"room_alias_name\":\"fika\",\"invite\":[\"@bo:kafe.example.test\"]}",
                    $"PUT {Room}/send/m.room.message/stomme* {{\"msgtype\":\"m.text\",\"body\":\"Fika at 3 - Kafé\",\"format\":\"org.matrix.custom.html\",\"formatted_body\":\"<b>Fika</b> at 3\"}}",
                    $"POST {Room}/invite {{\"user_id\":\"@cy:kafe.example.test\"}}",
                    $"GET {Room}/messages?dir=b&limit=100",
                    $"GET {Room}/messages?dir=b&limit=100&from=t2",
                    $"GET {Room}/members?membership=join",
                    $"POST {Room}/kick {{\"user_id\":\"@cy:kafe.example.test\",\"reason\":\"Wrong room\"}}",
                    $"POST {Room}/leave {{}}",
                }, received.Select(request => Regex.Replace(request.Line, "/stomme[0-9a-f]{32}", "/stomme*")));

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                string Outputs(string id) => JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>();
                Assert.Equal("!new:kafe.example.test", JsonNode.Parse(Outputs("room"))!["Result"]![0]!["room_id"]!.GetValue<string>());
                Assert.Equal("$sent1", JsonNode.Parse(Outputs("send"))!["Result"]![0]!["event_id"]!.GetValue<string>());
                Assert.Equal(120, JsonNode.Parse(Outputs("read"))!["RecordCount"]!.GetValue<int>()); // both pages, no more than asked
                Assert.Equal("Fika 1", JsonNode.Parse(Outputs("read"))!["Result"]![0]!["body"]!.GetValue<string>());
                Assert.Equal("Bo", JsonNode.Parse(Outputs("members"))!["Result"]![0]!["displayname"]!.GetValue<string>());
                Assert.True(JsonNode.Parse(Outputs("kick"))!["Result"]![0]!["kicked"]!.GetValue<bool>());
                Assert.True(JsonNode.Parse(Outputs("leave"))!["Result"]![0]!["left"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                Assert.DoesNotContain(token, graphBody, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for a Matrix homeserver that answers only a call carrying the token.</summary>
        private sealed class HomeserverStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly string _token;
            private readonly List<(string Line, bool Authorized)> _received = new();

            public HomeserverStandIn(string token)
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
                    var authorized = context.Request.Headers["Authorization"] == "Bearer " + _token;
                    lock (_gate)
                    {
                        _received.Add(($"{method} {raw}{(body.Length == 0 ? "" : " " + body)}", authorized));
                    }

                    static string Messages(int first, int count, string end) => "{\"start\":\"s\",\"end\":\"" + end + "\",\"chunk\":[" + string.Join(",", Enumerable.Range(first, count).Select(i => "{\"type\":\"m.room.message\",\"event_id\":\"$e" + i + "\",\"sender\":\"@ada:kafe.example.test\",\"content\":{\"msgtype\":\"m.text\",\"body\":\"Fika " + i + "\"}}")) + "]}";
                    const string Room = "/_matrix/client/v3/rooms/%21fika%3Akafe.example.test";
                    var path = raw.Split('?')[0];
                    var (status, answer) = !authorized ? (401, "{\"errcode\":\"M_UNKNOWN_TOKEN\",\"error\":\"Invalid access token passed.\"}")
                        : (method, path) switch
                        {
                            ("GET", "/_matrix/client/v3/account/whoami") => (200, "{\"user_id\":\"@ada:kafe.example.test\",\"device_id\":\"KAFE\"}"),
                            ("POST", "/_matrix/client/v3/createRoom") => (200, "{\"room_id\":\"!new:kafe.example.test\"}"),
                            ("PUT", var p) when p.StartsWith(Room + "/send/m.room.message/", StringComparison.Ordinal) => (200, "{\"event_id\":\"$sent1\"}"),
                            ("POST", Room + "/invite") or ("POST", Room + "/kick") or ("POST", Room + "/leave") => (200, "{}"),
                            ("GET", Room + "/messages") => (200, raw.Contains("from=t2", StringComparison.Ordinal) ? Messages(101, 80, "t3") : Messages(1, 100, "t2")),
                            ("GET", Room + "/members") => (200, "{\"chunk\":[{\"type\":\"m.room.member\",\"state_key\":\"@bo:kafe.example.test\",\"content\":{\"membership\":\"join\",\"displayname\":\"Bo\"}}]}"),
                            _ => (404, "{\"errcode\":\"M_UNRECOGNIZED\",\"error\":\"Unrecognized request\"}"),
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
