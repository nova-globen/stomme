#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// BL-229 - a Telegram bot that answers, DRIVEN on an installed SQLite host through the real task, port and connector against a
    /// loopback stand-in for the Bot API that keeps getUpdates' rules: a Telegram connection made over the API, its token vaulted
    /// and tested through the host's egress; a pipeline "Telegram message received" -> Send chat message, answering in the same
    /// chat in reply to the message with its text, published while a message is already waiting. A message sent after starts ONE
    /// run, by the host's own recurring task, and the bot answers it; the one waiting never runs; the token is in neither the
    /// pipeline's graph nor its run.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class TelegramBotDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task A_message_to_the_bot_starts_one_run_that_answers_it_and_one_waiting_at_the_publish_never_runs()
        {
            var token = "123456:drive" + Guid.NewGuid().ToString("N");
            using var bot = new BotApi();
            var settings = new JsonObject
            {
                ["Pipelines"] = new JsonObject { ["Enabled"] = true, ["TelegramTriggers"] = new JsonObject { ["IntervalSeconds"] = 5 } },
            };
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: settings);
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
                    Name = "Orders bot", ConnectorType = "telegram", Endpoint = $"http://127.0.0.1:{bot.Port}", TimeoutSeconds = 10, Credentials = new() { ["token"] = token },
                }, "the connection");
                Assert.True(made.Status == HttpStatusCode.OK, made.Body);
                var connection = JsonSerializer.Deserialize<ConnectionView>(made.Body, Json)!.Id.ToString();
                var tested = await Send(HttpMethod.Post, $"/api/Connections/{connection}/test", new { }, "test the connection");
                Assert.True(JsonNode.Parse(tested.Body)!["success"]!.GetValue<bool>(), tested.Body);

                bot.Add("Waiting before the publish");
                var created = await Send(HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Answer the bot" }, "create");
                var definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                var graph = new PipelineGraph
                {
                    Variables = { new PipelineGraphVariable { Id = "v-message", Name = "message" } },
                    Steps =
                    {
                        new PipelineGraphStep { Id = "bot", Type = PipelineTelegramEvent.StepType, Name = "Message in", Inputs = { ["Connection"] = connection }, OutputVariables = { ["Result"] = "v-message" } },
                        new PipelineGraphStep
                        {
                            Id = "answer", Type = "Stomme.SendChatMessage", Name = "Answer",
                            Inputs = { ["Connection"] = connection },
                            InputExpressions = { ["To"] = "{{ message.chat.id }}", ["Text"] = "You asked: {{ message.text }}", ["ReplyTo"] = "{{ message.messageId }}" },
                        },
                    },
                    Connections = { new PipelineGraphConnection { SourceId = "bot", TargetId = "answer" } },
                };
                var saved = await Send(HttpMethod.Put, $"/api/Pipelines/{definitionId}/graph", graph, "save the graph");
                Assert.True(saved.Status == HttpStatusCode.OK, saved.Body);
                var published = await Send(HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, "publish");
                Assert.True(JsonNode.Parse(published.Body)!["succeeded"]!.GetValue<bool>(), published.Body);

                var asked = bot.Add("Where is order 1001?");
                PipelineRunView? run = null;
                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (run is not { IsFinished: true } && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(500);
                    run = JsonSerializer.Deserialize<List<PipelineRunView>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}", output, "runs")).Body, Json)!.SingleOrDefault();
                }

                Assert.Equal("Finished", run?.SubStatus);
                var answer = Assert.Single(bot.Sent);
                Assert.Equal((777L, "You asked: Where is order 1001?", asked.MessageId),
                    (long.Parse(Convert.ToString(answer["chat_id"], CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture), (string?)answer["text"], (long?)answer["reply_parameters"]?["message_id"]));

                await Task.Delay(TimeSpan.FromSeconds(6)); // another look: the waiting message never runs, the answered one never again
                Assert.Single(JsonSerializer.Deserialize<List<PipelineRunView>>((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}", output, "runs")).Body, Json)!);
                Assert.Single(bot.Sent);

                Assert.DoesNotContain(token, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/{definitionId}/graph", output, "the graph")).Body, StringComparison.Ordinal);
                Assert.DoesNotContain(token, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs/{run!.Id}/steps", output, "the run's steps")).Body, StringComparison.Ordinal);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>A loopback stand-in for the Bot API: getMe, getUpdates as Telegram keeps it (an offset forgets the earlier updates, -1 keeps only the newest), sendMessage recorded.</summary>
        private sealed class BotApi : IDisposable
        {
            private readonly HttpListener _listener;
            private readonly object _gate = new();
            private readonly List<JsonObject> _waiting = new();
            private readonly List<JsonNode> _sent = new();
            private long _next = 800;

            public BotApi()
            {
                (_listener, Port) = LoopbackHttp.Start();
                _ = Task.Run(ServeAsync);
            }

            public int Port { get; }

            public IReadOnlyList<JsonNode> Sent
            {
                get
                {
                    lock (_gate)
                    {
                        return _sent.ToArray();
                    }
                }
            }

            public (long UpdateId, long MessageId) Add(string text)
            {
                lock (_gate)
                {
                    var id = ++_next;
                    _waiting.Add(new JsonObject
                    {
                        ["update_id"] = id,
                        ["message"] = new JsonObject
                        {
                            ["message_id"] = id - 700,
                            ["date"] = 1760000000 + id,
                            ["chat"] = new JsonObject { ["id"] = 777, ["type"] = "private", ["username"] = "ada_customer" },
                            ["from"] = new JsonObject { ["id"] = 777, ["is_bot"] = false, ["first_name"] = "Ada", ["username"] = "ada_customer" },
                            ["text"] = text,
                        },
                    });
                    return (id, id - 700);
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

                    var method = context.Request.Url!.AbsolutePath.Split('/').Last();
                    JsonObject answer;
                    lock (_gate)
                    {
                        switch (method)
                        {
                            case "getMe":
                                answer = new JsonObject { ["ok"] = true, ["result"] = new JsonObject { ["id"] = 42, ["is_bot"] = true, ["username"] = "orders_bot" } };
                                break;
                            case "getUpdates":
                                if (context.Request.QueryString["offset"] is { } text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
                                {
                                    if (offset < 0)
                                    {
                                        _waiting.RemoveRange(0, Math.Max(0, _waiting.Count - 1));
                                    }
                                    else
                                    {
                                        _waiting.RemoveAll(update => update["update_id"]!.GetValue<long>() < offset);
                                    }
                                }

                                answer = new JsonObject { ["ok"] = true, ["result"] = new JsonArray(_waiting.Select(update => (JsonNode)update.DeepClone()).ToArray()) };
                                break;
                            case "sendMessage":
                                _sent.Add(JsonNode.Parse(body)!);
                                answer = new JsonObject { ["ok"] = true, ["result"] = new JsonObject { ["message_id"] = 9000 + _sent.Count, ["chat"] = new JsonObject { ["id"] = 777 } } };
                                break;
                            default:
                                answer = new JsonObject { ["ok"] = false, ["description"] = "Not Found" };
                                break;
                        }
                    }

                    var bytes = Encoding.UTF8.GetBytes(answer.ToJsonString());
                    context.Response.StatusCode = answer["ok"]!.GetValue<bool>() ? 200 : 404;
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
