#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Engine.Common.Services;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Features;
using Stomme.Engine.KeyValues.Models;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps.Flowchart.Steps;
using Stomme.Models.Pipelines;
using Stomme.Modules.Connectors.Messaging;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Stomme.Tests.Connectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// n8n's Telegram Trigger as "Telegram message received": a published pipeline on a bot is started once for each
    /// message or channel post the bot gets - the message its trigger's result - and never for one waiting when it was published
    /// (Telegram told to forget them); any other kind of update is moved past; every pipeline on one bot gets each message from
    /// ONE ask; at most so many a look; a bot the publish could not ask recorded by the next look; a bot with a webhook set
    /// starting nothing; a bot no trigger watches forgotten. The engine with the module's step, task and handler; the port runs
    /// the real Telegram connector against a scripted Bot API that keeps getUpdates' rules.
    /// </summary>
    public sealed class TelegramTriggerTests : IAsyncLifetime
    {
        private static readonly Guid ConnectionId = Guid.Parse("2c7d9e1f-4a6b-4c8d-9e0f-1a2b3c4d5e6f");
        private readonly PipelineTriggerOptions _options = new();
        private readonly ScriptedBotApi _bot = new();
        private TelegramPort _port = default!;
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            _port = new TelegramPort(_bot);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineTriggerOptions>>(Options.Create(_options));
            services.AddEngine(module =>
            {
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<KeyValueFeature>(_ => { });
            });
            services.AddSingleton(typeof(MemoryStore<>));
            services.AddSingleton<IConnectorExtractionService>(_port);
            services.AddNotificationHandler<KeepRunInput, Stomme.Engine.Pipelines.Notifications.PipelineStarted>();
            services.AddScoped<WatchedTelegramBots>();
            services.AddScoped<WatchedTelegramBotsTask>();
            services.AddNotificationHandler<StartWatchingTelegramBotOnPublish, Stomme.Engine.Pipelines.Runtime.Notifications.PipelineTriggersIndexed>();
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        /// <summary>The Bot API's getUpdates as Telegram keeps it: an offset forgets every update before it; -1 keeps only the newest.</summary>
        private sealed class ScriptedBotApi : HttpMessageHandler
        {
            private readonly List<JsonObject> _waiting = new();
            private long _next = 500;

            public ConcurrentQueue<string> Asked { get; } = new();

            public bool Webhook { get; set; }

            public long Add(string text, long chat = 777, string kind = "message")
            {
                var id = ++_next;
                var message = new JsonObject
                {
                    ["message_id"] = id - 400,
                    ["date"] = 1760000000 + id,
                    ["chat"] = new JsonObject { ["id"] = chat, ["type"] = kind == "channel_post" ? "channel" : "private", ["title"] = kind == "channel_post" ? "Orders" : null },
                    ["text"] = text,
                };
                if (kind != "channel_post")
                {
                    message["from"] = new JsonObject { ["id"] = chat, ["is_bot"] = false, ["first_name"] = "Ada", ["username"] = "ada_customer" };
                }

                lock (_waiting)
                {
                    _waiting.Add(new JsonObject { ["update_id"] = id, [kind] = message });
                }

                return id;
            }

            public int Waiting
            {
                get
                {
                    lock (_waiting)
                    {
                        return _waiting.Count;
                    }
                }
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var query = (request.RequestUri!.Query ?? "").TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Select(pair => pair.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair.Length > 1 ? pair[1] : "");
                Asked.Enqueue(request.RequestUri.AbsolutePath.Split('/').Last() + (query.TryGetValue("offset", out var asked) ? " " + asked : ""));
                if (Webhook)
                {
                    return Answer(HttpStatusCode.Conflict, new JsonObject { ["ok"] = false, ["error_code"] = 409, ["description"] = "Conflict: can't use getUpdates method while webhook is active; use deleteWebhook to delete the webhook first" });
                }

                var limit = query.TryGetValue("limit", out var most) ? int.Parse(most, CultureInfo.InvariantCulture) : 100;
                List<JsonObject> answered;
                lock (_waiting)
                {
                    if (query.TryGetValue("offset", out var text) && long.Parse(text, CultureInfo.InvariantCulture) is var offset)
                    {
                        if (offset < 0)
                        {
                            _waiting.RemoveRange(0, Math.Max(0, _waiting.Count + (int)offset)); // -1: only the newest stays
                        }
                        else
                        {
                            _waiting.RemoveAll(update => update["update_id"]!.GetValue<long>() < offset);
                        }
                    }

                    answered = _waiting.Take(limit).Select(update => (JsonObject)update.DeepClone()).ToList();
                }

                return Answer(HttpStatusCode.OK, new JsonObject { ["ok"] = true, ["result"] = new JsonArray(answered.ToArray<JsonNode>()) });
            }

            private static Task<HttpResponseMessage> Answer(HttpStatusCode status, JsonObject body) =>
                Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json") });
        }

        /// <summary>The extraction port as the Connectivity module would answer it: the real Telegram connector against the scripted Bot API, up to the cap.</summary>
        private sealed class TelegramPort(ScriptedBotApi bot) : IConnectorExtractionService
        {
            public ConcurrentQueue<string> Operations { get; } = new();

            public bool Down { get; set; }

            public async Task<ConnectorExtractionResult> ExtractAsync(Guid connectionId, string? operation, IReadOnlyDictionary<string, string> parameters, int maxRecords = ConnectorExtractionLimits.DefaultMaxRecords, CancellationToken cancellationToken = default)
            {
                Operations.Enqueue(operation!);
                if (Down || connectionId != ConnectionId)
                {
                    return ConnectorExtractionResult.Fail(ConnectorOutcome.Failed);
                }

                var context = Contexts.Extract(new StubEgress(bot), "https://api.telegram.org", operation!, parameters, credentials: new Dictionary<string, string> { ["token"] = "123456:test-only" });
                var records = new List<ConnectorRecord>();
                await foreach (var record in new TelegramConnector().ExtractAsync(context, cancellationToken))
                {
                    if (records.Count == maxRecords)
                    {
                        return ConnectorExtractionResult.Ok(records, truncated: true, maxRecords); // read one past the cap, as the real port does
                    }

                    records.Add(record);
                }

                return ConnectorExtractionResult.Ok(records, truncated: false, maxRecords);
            }
        }

        private static async Task<string> PublishAsync(IServiceProvider services, string name = "Answer the bot", string connection = "2c7d9e1f-4a6b-4c8d-9e0f-1a2b3c4d5e6f")
        {
            var trigger = new TelegramMessageReceived { Id = "bot", Connection = new(connection) };
            trigger.SetCanStartPipeline(true);
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(new Flowchart { Id = "flow", Steps = { trigger } });
            definition.Name = name;
            Assert.True((await publisher.PublishAsync(definition)).Succeeded);
            return definition.DefinitionId;
        }

        // The messages each run of the pipeline was started with, oldest run first.
        private static async Task<JsonNode[]> StartedWithAsync(IServiceProvider services, string definitionId) =>
            (await services.GetRequiredService<IPipelineInstanceStore>().FindManyAsync(new PipelineInstanceFilter { DefinitionId = definitionId }))
                .OrderBy(run => run.CreatedAt)
                .Select(run => JsonNode.Parse(KeepRunInput.Of(run.PipelineState)[TelegramMessageReceived.MessageInputKey].ToString()!)!)
                .ToArray();

        private static string[] Texts(JsonNode[] messages) => messages.Select(message => message["text"]!.GetValue<string>()).ToArray();

        [Fact]
        public async Task A_message_starts_one_run_with_it_and_one_waiting_at_the_publish_does_not()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var task = services.GetRequiredService<WatchedTelegramBotsTask>();
            _bot.Add("There first");
            _bot.Add("And this");
            var watching = await PublishAsync(services);
            Assert.Equal(new[] { "latest" }, _port.Operations); // the publish asked for the newest - Telegram forgets the rest
            Assert.Equal(1, _bot.Waiting);

            var update = _bot.Add("Where is order 1001?");
            await task.ExecuteAsync(CancellationToken.None);
            await task.ExecuteAsync(CancellationToken.None);
            var started = await StartedWithAsync(services, watching);
            Assert.Equal(new[] { "Where is order 1001?" }, Texts(started));
            var message = started[0];
            Assert.Equal((update, "message", 777L, "private", "ada_customer", "Ada"),
                (message["updateId"]!.GetValue<long>(), message["kind"]!.GetValue<string>(), message["chat"]!["id"]!.GetValue<long>(), message["chat"]!["type"]!.GetValue<string>(),
                    message["from"]!["username"]!.GetValue<string>(), message["from"]!["firstName"]!.GetValue<string>()));
            Assert.Equal(update - 400, message["messageId"]!.GetValue<long>());
            Assert.Equal(0, _bot.Waiting); // asking from the next offset told Telegram it was handed on
        }

        [Fact]
        public async Task A_channel_post_starts_a_run_and_another_kind_of_update_is_moved_past()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var task = services.GetRequiredService<WatchedTelegramBotsTask>();
            var watching = await PublishAsync(services);
            _bot.Add("New stock", chat: -100123, kind: "channel_post");
            _bot.Add("an edit", kind: "edited_message");
            _bot.Add("After the edit");

            await task.ExecuteAsync(CancellationToken.None);
            var started = await StartedWithAsync(services, watching);
            Assert.Equal(new[] { "New stock", "After the edit" }, Texts(started));
            Assert.Equal(("channel_post", -100123L, "Orders", (JsonNode?)null), (started[0]["kind"]!.GetValue<string>(), started[0]["chat"]!["id"]!.GetValue<long>(), started[0]["chat"]!["title"]!.GetValue<string>(), started[0]["from"]));
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(2, (await StartedWithAsync(services, watching)).Length); // the edit is never come back to
        }

        [Fact]
        public async Task Every_pipeline_on_one_bot_gets_each_message_from_one_ask_and_a_look_starts_at_most_so_many()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var task = services.GetRequiredService<WatchedTelegramBotsTask>();
            _options.TelegramTriggers.MaxMessagesPerLook = 2;
            var first = await PublishAsync(services, "First");
            var second = await PublishAsync(services, "Second");
            _bot.Add("One");
            _bot.Add("Two");
            _bot.Add("Three");

            while (_bot.Asked.TryDequeue(out _))
            {
            }

            await task.ExecuteAsync(CancellationToken.None);
            Assert.Single(_bot.Asked); // one ask for the bot, whatever the pipelines on it
            Assert.Equal(new[] { "One", "Two" }, Texts(await StartedWithAsync(services, first)));
            Assert.Equal(new[] { "One", "Two" }, Texts(await StartedWithAsync(services, second)));

            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(new[] { "One", "Two", "Three" }, Texts(await StartedWithAsync(services, first)));
            Assert.Equal(new[] { "One", "Two", "Three" }, Texts(await StartedWithAsync(services, second)));
        }

        [Fact]
        public async Task A_bot_the_publish_could_not_ask_is_recorded_by_the_next_look_and_one_with_a_webhook_starts_nothing()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var task = services.GetRequiredService<WatchedTelegramBotsTask>();
            _port.Down = true;
            var watching = await PublishAsync(services);
            _port.Down = false;
            _bot.Add("While it could not be asked");

            await task.ExecuteAsync(CancellationToken.None); // records, starts nothing
            Assert.Empty(await StartedWithAsync(services, watching));
            _bot.Webhook = true;
            _bot.Add("While a webhook was set");
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Empty(await StartedWithAsync(services, watching));

            _bot.Webhook = false; // deleted: what came meanwhile is still waiting, and starts its run
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(new[] { "While a webhook was set" }, Texts(await StartedWithAsync(services, watching)));
        }

        [Fact]
        public async Task A_bot_with_a_webhook_at_the_publish_records_nothing_so_what_waits_then_still_starts_nothing()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var task = services.GetRequiredService<WatchedTelegramBotsTask>();
            _bot.Webhook = true;
            _bot.Add("Waiting at the publish");
            _bot.Add("Also waiting");
            var watching = await PublishAsync(services);
            Assert.Empty(await services.GetRequiredService<IKeyValueStore>().FindManyAsync(new KeyValueFilter { Key = WatchedTelegramBots.SeenKeyPrefix, StartsWith = true }, CancellationToken.None));

            _bot.Webhook = false;
            await task.ExecuteAsync(CancellationToken.None); // records where the bot is now: what waited starts nothing
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Empty(await StartedWithAsync(services, watching));
            _bot.Add("After");
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(new[] { "After" }, Texts(await StartedWithAsync(services, watching)));
        }

        [Fact]
        public async Task A_bot_no_trigger_watches_is_forgotten_and_the_connection_is_held_to_the_rules()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var keyValues = services.GetRequiredService<IKeyValueStore>();
            await PublishAsync(services);
            var stale = WatchedTelegramBots.SeenKey(null, "a-hash-no-trigger-has");
            await keyValues.SaveAsync(new Stomme.Engine.KeyValues.Entities.SerializedKeyValuePair { Key = stale, SerializedValue = "{}" }, CancellationToken.None);
            await services.GetRequiredService<WatchedTelegramBotsTask>().ExecuteAsync(CancellationToken.None);
            var kept = (await keyValues.FindManyAsync(new KeyValueFilter { Key = WatchedTelegramBots.SeenKeyPrefix, StartsWith = true }, CancellationToken.None)).Select(pair => pair.Key).ToList();
            Assert.DoesNotContain(stale, kept);
            Assert.Single(kept);

            Assert.Equal("'orders-bot' is not a connection id - choose one of the site's Telegram connections.", TelegramMessageReceived.ConnectionProblem("orders-bot"));
            Assert.Equal(new TelegramMessageStimulus(ConnectionId.ToString("D")), TelegramMessageReceived.Stimulus(" " + ConnectionId.ToString("B") + " "));
            var graph = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "bot", Type = PipelineTelegramEvent.StepType, Inputs = { ["Connection"] = "orders-bot" } } } };
            Assert.StartsWith("'orders-bot' is not a connection id", Assert.Throws<ArgumentException>(() =>
                PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>())).Message);
        }

        [Fact]
        public void The_sample_is_a_message_a_person_sent_the_bot()
        {
            var sample = JsonNode.Parse(PipelineTelegramEvent.Sample(new DateTimeOffset(2026, 10, 11, 9, 30, 0, TimeSpan.Zero)))!;
            Assert.Equal(("Where is order 1001?", 123456789L, "private", "ada_customer", 42L, "2026-10-11T09:30:00Z"),
                (sample["text"]!.GetValue<string>(), sample["chat"]!["id"]!.GetValue<long>(), sample["chat"]!["type"]!.GetValue<string>(), sample["from"]!["username"]!.GetValue<string>(),
                    sample["messageId"]!.GetValue<long>(), sample["date"]!.GetValue<string>()));
        }
    }
}
