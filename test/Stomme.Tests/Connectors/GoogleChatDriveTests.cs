#nullable enable
using System;
using System.Collections.Concurrent;
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
    /// BL-229 - Google Chat DRIVEN on an installed SQLite host, with the REAL connector, the host's egress and the extraction port,
    /// against a loopback stand-in for Google's token endpoint and the Chat API that refuses a call without the token it issued: a
    /// Google Chat connection made over the API, the client's secret and refresh token vaulted, tested; a published pipeline SENDS
    /// a message through Send chat message - the chat connectors' step - over two lines, lists the spaces, edits the message,
    /// reads the space's members and deletes the message; the secret and the refresh token in neither the graph nor the run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class GoogleChatDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>A loopback stand-in for Google's token endpoint and the Chat API: its token for one refresh grant, every call kept.</summary>
        private sealed class GoogleChatStandIn : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly string _form;

            public GoogleChatStandIn(string clientId, string secret, string refresh)
            {
                _form = $"grant_type=refresh_token&refresh_token={Uri.EscapeDataString(refresh)}&client_id={Uri.EscapeDataString(clientId)}&client_secret={Uri.EscapeDataString(secret)}";
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public ConcurrentQueue<(string Method, string Path, string Body)> Received { get; } = new();

            public void Dispose() => _listener.Close();

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

                    using var reader = new StreamReader(context.Request.InputStream);
                    var body = await reader.ReadToEndAsync();
                    var path = context.Request.RawUrl ?? "/";
                    var method = context.Request.HttpMethod;
                    Received.Enqueue((method, path, body));
                    var (status, answer) = (method, path) switch
                    {
                        ("POST", "/token") => body == _form ? (200, "{\"access_token\":\"chat-drive-token\",\"expires_in\":3600}") : (400, "{\"error\":\"invalid_grant\"}"),
                        _ when context.Request.Headers["Authorization"] != "Bearer chat-drive-token" => (401, "{\"error\":{\"code\":401,\"message\":\"Request had invalid authentication credentials.\",\"status\":\"UNAUTHENTICATED\"}}"),
                        ("POST", "/v1/spaces/AAAAkassa/messages") => (200, "{\"name\":\"spaces/AAAAkassa/messages/M1.M1\",\"text\":" + JsonSerializer.Serialize((string?)JsonNode.Parse(body)!["text"]) + ",\"thread\":{\"name\":\"spaces/AAAAkassa/threads/T1\"}}"),
                        ("GET", var p) when p.StartsWith("/v1/spaces?", StringComparison.Ordinal) => (200, "{\"spaces\":[{\"name\":\"spaces/AAAAkassa\",\"displayName\":\"Kassa\",\"spaceType\":\"SPACE\"},{\"name\":\"spaces/BBBBlager\",\"displayName\":\"Lager\",\"spaceType\":\"SPACE\"}]}"),
                        ("PATCH", "/v1/spaces/AAAAkassa/messages/M1.M1?updateMask=text") => (200, "{\"name\":\"spaces/AAAAkassa/messages/M1.M1\",\"text\":" + JsonSerializer.Serialize((string?)JsonNode.Parse(body)!["text"]) + "}"),
                        ("GET", var p) when p.StartsWith("/v1/spaces/AAAAkassa/members?", StringComparison.Ordinal) => (200, "{\"memberships\":[{\"name\":\"spaces/AAAAkassa/members/1\",\"member\":{\"name\":\"users/1\",\"type\":\"HUMAN\"},\"role\":\"ROLE_MANAGER\"}]}"),
                        ("DELETE", "/v1/spaces/AAAAkassa/messages/M1.M1") => (200, "{}"),
                        _ => (404, "{\"error\":{\"code\":404,\"message\":\"Not found.\",\"status\":\"NOT_FOUND\"}}"),
                    };
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(answer);
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            }
        }

        [Fact]
        public async Task A_pipeline_sends_lists_edits_and_deletes_through_google_chat()
        {
            using var chat = new GoogleChatStandIn("kafe-client", "chat-secret-drive", "chat-refresh-drive");
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
                    Name = "Kafé chat", ConnectorType = "googlechat", Endpoint = $"http://127.0.0.1:{chat.Port}", TimeoutSeconds = 10,
                    Settings = new() { ["token_url"] = $"http://127.0.0.1:{chat.Port}/token" },
                    Credentials = new() { ["client-id"] = "kafe-client", ["client-secret"] = "chat-secret-drive", ["refresh-token"] = "chat-refresh-drive" },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kassa chat" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                PipelineGraphStep Call(string id, string operation, string parameters) =>
                    new() { Id = id, Type = "Stomme.ExtractFromConnection", Name = id, Inputs = { ["Connection"] = connection, ["Operation"] = operation, ["Parameters"] = parameters } };
                var graph = new PipelineGraph
                {
                    Steps =
                    {
                        new() { Id = "tell", Type = "Stomme.SendChatMessage", Name = "tell", Inputs = { ["Connection"] = connection, ["To"] = "AAAAkassa", ["Text"] = "Kassan stängd\nÅter 08:00" } },
                        Call("spaces", "spaces", "max = 10"),
                        Call("edit", "message_update", "message = spaces/AAAAkassa/messages/M1.M1\ntext = Kassan öppen"),
                        Call("members", "members", "space = AAAAkassa"),
                        Call("remove", "message_delete", "message = spaces/AAAAkassa/messages/M1.M1"),
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
                for (var attempt = 0; attempt < 300 && run?.IsFinished != true; attempt++)
                {
                    var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                    run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                    await Task.Delay(100);
                }

                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.True(run?.SubStatus == "Finished", steps);
                var sent = chat.Received.Single(request => request.Method == "POST" && request.Path.EndsWith("/messages", StringComparison.Ordinal));
                Assert.Equal("Kassan stängd\nÅter 08:00", (string?)JsonNode.Parse(sent.Body)!["text"]); // Send chat message's text, over two lines
                Assert.Contains("spaces/AAAAkassa/messages/M1.M1", steps, StringComparison.Ordinal); // the message's name its step's result
                JsonNode Outputs(string id) => JsonNode.Parse(JsonNode.Parse(steps)!.AsArray().Single(step => step!["stepId"]!.GetValue<string>() == id)!["outputsJson"]!.GetValue<string>())!;
                Assert.Equal(new[] { "Kassa", "Lager" }, Outputs("spaces")["Result"]!.AsArray().Select(space => space!["displayName"]!.GetValue<string>()));
                Assert.Equal("Kassan öppen", Outputs("edit")["Result"]![0]!["text"]!.GetValue<string>());
                Assert.Equal("ROLE_MANAGER", Outputs("members")["Result"]![0]!["role"]!.GetValue<string>());
                Assert.True(Outputs("remove")["Result"]![0]!["deleted"]!.GetValue<bool>());
                var graphBody = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body;
                foreach (var secret in new[] { "chat-secret-drive", "chat-refresh-drive" })
                {
                    Assert.DoesNotContain(secret, steps, StringComparison.Ordinal);
                    Assert.DoesNotContain(secret, graphBody, StringComparison.Ordinal);
                }
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
