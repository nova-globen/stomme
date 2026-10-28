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
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - Send chat message DRIVEN on an installed SQLite host with the REAL Slack, Telegram, Discord, Mattermost, Twilio and
    /// Microsoft Teams connectors against a loopback stand-in for all six: loopback allowed by the Host, one connection per service
    /// made over the API with its token vaulted (Twilio's SID and auth token, its From a setting; Teams' client secret and refresh
    /// token, exchanged at its token endpoint) and tested through the host's own egress; a published pipeline sends one message
    /// through each - each service got it at its own address, authorised its own
    /// way, the run finished with each message's id - and a message to a channel Slack does not know ends the run's step in
    /// Slack's words; no token is in the pipeline's graph or its run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class ChatMessageDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_pipeline_sends_through_each_chat_service_with_its_vaulted_token_and_a_refusal_is_said_in_the_services_words()
        {
            var slackToken = "xoxb-" + Guid.NewGuid().ToString("N");
            var telegramToken = "123456:" + Guid.NewGuid().ToString("N");
            var discordToken = "discord-" + Guid.NewGuid().ToString("N");
            var mattermostToken = "mm" + Guid.NewGuid().ToString("N");
            var twilioSid = "AC" + Guid.NewGuid().ToString("N");
            var twilioToken = "tw" + Guid.NewGuid().ToString("N");
            var teamsSecret = "ts" + Guid.NewGuid().ToString("N");
            var teamsRefresh = "tr" + Guid.NewGuid().ToString("N");
            var whatsAppToken = "EAAG" + Guid.NewGuid().ToString("N");
            using var server = new ChatServer();
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

                // The catalog offers the services, under Messaging.
                var connectors = (await GateDrive.GetAsync(client, cookies, Host, "/api/Connections/connectors", output, "the connectors")).Body;
                foreach (var type in new[] { "slack", "telegram", "discord", "mattermost", "twilio", "teams", "whatsapp" })
                {
                    Assert.Contains($"\"connectorType\":\"{type}\"", connectors);
                }

                Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Put, "/api/Connections/egress", new ConnectionEgressRequest { AllowedHosts = "127.0.0.1" }, "the Host allows loopback")).Status);
                async Task<Guid> ConnectionAsync(string name, string type, string path, string token, Dictionary<string, string>? credentials = null, Dictionary<string, string>? settings = null)
                {
                    var made = await Send(HttpMethod.Post, "/api/Connections", new ConnectionRequest
                    {
                        Name = name, ConnectorType = type, Endpoint = $"http://127.0.0.1:{server.Port}/{path}", TimeoutSeconds = 10, Credentials = credentials ?? new() { ["token"] = token }, Settings = settings,
                    }, name);
                    Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                    var id = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id;
                    var tested = await Send(HttpMethod.Post, $"/api/Connections/{id}/test", new { }, "test " + name);
                    Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);
                    return id;
                }

                var slack = await ConnectionAsync("Team Slack", "slack", "slack/api", slackToken);
                var telegram = await ConnectionAsync("Orders bot", "telegram", "telegram", telegramToken);
                var discord = await ConnectionAsync("Ops Discord", "discord", "discord/api/v10", discordToken);
                var mattermost = await ConnectionAsync("Team Mattermost", "mattermost", "mattermost", mattermostToken);
                var twilio = await ConnectionAsync("Order texts", "twilio", "twilio", "", new() { ["username"] = twilioSid, ["password"] = twilioToken }, new() { ["from"] = "+15017122661" });
                var teams = await ConnectionAsync("Ops Teams", "teams", "graph/v1.0", "", new() { ["client-id"] = "app-1", ["client-secret"] = teamsSecret, ["refresh-token"] = teamsRefresh },
                    new() { ["token_url"] = $"http://127.0.0.1:{server.Port}/teams-login/token" });
                var whatsApp = await ConnectionAsync("Order chats", "whatsapp", "whatsapp/v23.0", whatsAppToken, settings: new() { ["phone_number_id"] = "106540352242922" });

                async Task<(string RunId, PipelineRunView? Run)> RunAsync(string name, params PipelineGraphStep[] steps)
                {
                    var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = name }, "create");
                    var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                    var graph = new PipelineGraph { Steps = steps.ToList() };
                    for (var i = 1; i < steps.Length; i++)
                    {
                        graph.Connections.Add(new PipelineGraphConnection { SourceId = steps[i - 1].Id, SourcePort = "Done", TargetId = steps[i].Id });
                    }

                    var saved = await Send(HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, "save the graph");
                    Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);
                    var published = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish");
                    Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);
                    var started = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest(), "start");
                    var runId = JsonNode.Parse(started.Body)!["runId"]!.GetValue<string>();
                    PipelineRunView? run = null;
                    for (var attempt = 0; attempt < 150 && run?.IsFinished != true; attempt++)
                    {
                        var read = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}", output, "the run");
                        run = read.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<PipelineRunView>(read.Body, Json) : null;
                        await Task.Delay(100);
                    }

                    return (runId, run);
                }

                static PipelineGraphStep Chat(string id, Guid connection, string to, string text) =>
                    new() { Id = id, Type = "Stomme.SendChatMessage", Name = id, Inputs = { ["Connection"] = connection.ToString(), ["To"] = to, ["Text"] = text } };

                var (runId, run) = await RunAsync("Tell everyone",
                    Chat("slack", slack, "#orders", "Order 42 shipped"),
                    Chat("telegram", telegram, "-1001234", "Order 42 shipped"),
                    Chat("discord", discord, "1090000000000000001", "Order 42 shipped"),
                    Chat("mattermost", mattermost, "4xp9fdt7pbgium38k5k6w95oxh", "Order 42 shipped"),
                    Chat("twilio", twilio, "+46701740605", "Order 42 shipped"),
                    Chat("teams", teams, "4e6f7661-476c-6f62-656e-414220202020/19:a1b2c3d4e5@thread.tacv2", "Order 42 shipped"),
                    Chat("whatsapp", whatsApp, "+46 70 174 06 05", "Order 42 shipped"));
                Assert.Equal("Finished", run?.SubStatus);

                // Each service got it at its own address, authorised its own way.
                var sent = server.Received.Where(request => request.Path.EndsWith("chat.postMessage") || request.Path.EndsWith("sendMessage") || request.Path.EndsWith("/messages")
                    || request.Path.EndsWith("/posts") || request.Path.EndsWith("/Messages.json")).ToList();
                Assert.Equal(7, sent.Count);
                Assert.Equal(("/slack/api/chat.postMessage", "Bearer " + slackToken, "#orders"), (sent[0].Path, sent[0].Authorization, (string?)JsonNode.Parse(sent[0].Body)!["channel"]));
                Assert.Equal(($"/telegram/bot{telegramToken}/sendMessage", (string?)null, "-1001234"), (sent[1].Path, sent[1].Authorization, (string?)JsonNode.Parse(sent[1].Body)!["chat_id"]));
                Assert.Equal(("/discord/api/v10/channels/1090000000000000001/messages", "Bot " + discordToken, "Order 42 shipped"), (sent[2].Path, sent[2].Authorization, (string?)JsonNode.Parse(sent[2].Body)!["content"]));
                Assert.Equal(("/mattermost/api/v4/posts", "Bearer " + mattermostToken, "4xp9fdt7pbgium38k5k6w95oxh"), (sent[3].Path, sent[3].Authorization, (string?)JsonNode.Parse(sent[3].Body)!["channel_id"]));
                Assert.Equal(($"/twilio/2010-04-01/Accounts/{twilioSid}/Messages.json", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(twilioSid + ":" + twilioToken))),
                    (sent[4].Path, sent[4].Authorization));
                Assert.Equal("To=%2B46701740605&Body=Order+42+shipped&From=%2B15017122661", sent[4].Body); // a form, From the connection's setting
                Assert.Equal(("Bearer access-teams", "Order 42 shipped"), (sent[5].Authorization, (string?)JsonNode.Parse(sent[5].Body)!["body"]!["content"]));
                Assert.Contains("/graph/v1.0/teams/4e6f7661-476c-6f62-656e-414220202020/channels/", sent[5].Path);
                Assert.Contains("refresh_token=" + teamsRefresh, server.Received.First(request => request.Path == "/teams-login/token").Body); // exchanged at the tenant's endpoint
                Assert.Equal(("/whatsapp/v23.0/106540352242922/messages", "Bearer " + whatsAppToken, "46701740605"), (sent[6].Path, sent[6].Authorization, (string?)JsonNode.Parse(sent[6].Body)!["to"])); // from the connection's number

                // Each message's id is its step's result.
                var steps = (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/steps", output, "the run's steps")).Body;
                Assert.Contains("1700000000.000100", steps);
                Assert.Contains("1100000000000000001", steps);
                Assert.Contains("pq1mk6xn3fdq8bw5a7ygkm1z8e", steps);
                Assert.Contains("SM0123456789abcdef0123456789abcdef", steps);
                Assert.Contains("1760000000001", steps);
                Assert.Contains("wamid.HBgLNDY3MDEyMzQ1NjcVAgARGBI", steps);
                foreach (var token in new[] { slackToken, telegramToken, discordToken, mattermostToken, twilioToken, teamsSecret, teamsRefresh, whatsAppToken })
                {
                    Assert.DoesNotContain(token, steps, StringComparison.Ordinal);
                    Assert.DoesNotContain(token, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{runId}/log", output, "the run's log")).Body, StringComparison.Ordinal);
                }

                // A channel Slack does not know: the step fails in Slack's words.
                var (refusedId, refused) = await RunAsync("Tell nobody", Chat("slack", slack, "#nowhere", "Hello?"));
                Assert.True(refused?.IsFinished, $"the run did not end: {refused?.Status} {refused?.SubStatus}");
                Assert.NotEqual("Finished", refused!.SubStatus);
                var refusedStep = Assert.Single(JsonSerializer.Deserialize<List<PipelineStepRunView>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{refusedId}/steps", output, "the refused run's steps")).Body, Json)!);
                Assert.Equal(("Faulted", "Slack: channel_not_found"), (refusedStep.Status, refusedStep.Error));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for Slack's, Telegram's, Discord's, Mattermost's and Twilio's APIs, each under its name, recording each request.</summary>
        private sealed class ChatServer : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<(string Method, string Path, string? Authorization, string Body)> _received = new();

            public ChatServer()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<(string Method, string Path, string? Authorization, string Body)> Received
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

                    var path = context.Request.Url!.AbsolutePath;
                    lock (_gate)
                    {
                        _received.Add((context.Request.HttpMethod, path, context.Request.Headers["Authorization"], body));
                    }

                    var (status, answer) = path switch
                    {
                        _ when path.EndsWith("/auth.test", StringComparison.Ordinal) => (200, """{"ok":true,"user":"orders-bot","team":"Acme"}"""),
                        _ when path.EndsWith("/chat.postMessage", StringComparison.Ordinal) => body.Contains("#nowhere", StringComparison.Ordinal)
                            ? (200, """{"ok":false,"error":"channel_not_found"}""")
                            : (200, """{"ok":true,"channel":"C0123","ts":"1700000000.000100"}"""),
                        _ when path.EndsWith("/getMe", StringComparison.Ordinal) => (200, """{"ok":true,"result":{"username":"orders_bot"}}"""),
                        _ when path.EndsWith("/sendMessage", StringComparison.Ordinal) => (200, """{"ok":true,"result":{"message_id":77,"chat":{"id":-1001234}}}"""),
                        _ when path.EndsWith("/users/@me", StringComparison.Ordinal) => (200, """{"username":"Ops"}"""),
                        _ when path == "/teams-login/token" => (200, """{"access_token":"access-teams","expires_in":3600,"token_type":"Bearer"}"""),
                        _ when path == "/graph/v1.0/me" => (200, """{"displayName":"Ops Bot"}"""),
                        _ when path.StartsWith("/graph/v1.0/teams/", StringComparison.Ordinal) && path.EndsWith("/messages", StringComparison.Ordinal) => (201, """{"id":"1760000000001"}"""),
                        _ when path == "/whatsapp/v23.0/106540352242922/messages" => (200, """{"messaging_product":"whatsapp","contacts":[{"input":"46701740605","wa_id":"46701740605"}],"messages":[{"id":"wamid.HBgLNDY3MDEyMzQ1NjcVAgARGBI"}]}"""),
                        _ when path == "/whatsapp/v23.0/106540352242922" => (200, """{"display_phone_number":"+1 555-0100","verified_name":"Acme Orders","id":"106540352242922"}"""),
                        _ when path.EndsWith("/messages", StringComparison.Ordinal) => (200, """{"id":"1100000000000000001","channel_id":"1090000000000000001"}"""),
                        _ when path.EndsWith("/api/v4/users/me", StringComparison.Ordinal) => (200, """{"id":"u1","username":"orders-bot"}"""),
                        _ when path.EndsWith("/api/v4/posts", StringComparison.Ordinal) => (201, """{"id":"pq1mk6xn3fdq8bw5a7ygkm1z8e","channel_id":"4xp9fdt7pbgium38k5k6w95oxh","message":"Order 42 shipped"}"""),
                        _ when path.EndsWith("/Messages.json", StringComparison.Ordinal) => (201, """{"sid":"SM0123456789abcdef0123456789abcdef","to":"+46701740605","status":"queued"}"""),
                        _ when path.Contains("/2010-04-01/Accounts/", StringComparison.Ordinal) => (200, """{"friendly_name":"Acme orders","status":"active"}"""),
                        _ => (404, """{"message":"not here"}"""),
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
