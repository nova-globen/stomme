#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.KeyValues.Features;
using Stomme.Engine.KeyValues.Models;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Models.Pipelines;
using Stomme.Engine.Pipelines.Runtime.Models;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Steps;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Endpoint = Stomme.Engine.Pipelines.Steps.Flowchart.Models.Endpoint;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-95 increment (e): the schedule and webhook TRIGGERS through the engine as the host composes it - a published
    /// pipeline that starts with one is indexed into the trigger store, the schedules task fires a due cron exactly
    /// once per occurrence against a controlled clock (anchored on first sight, so publishing replays nothing; a missed
    /// occurrence fires once), and a webhook stimulus starts the pipeline with the body as the trigger's result.
    /// </summary>
    public sealed class PipelineTriggersTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly StringWriter _console = new();
        private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 26, 10, 0, 30, TimeSpan.Zero));
        private readonly PipelineTriggerOptions _options = new();

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ResultSink>();
            services.AddSingleton<IOptions<PipelineTriggerOptions>>(Options.Create(_options));
            var console = _console;
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new CapturedOut(console));
                module.AddStepsFrom<PipelineStepsMarker>();
                module.AddStep<Capture>();
                module.AddStep<Boom>();
                // The schedules task keeps its last-fire marks in the engine's key-value store, which the host composes
                // through its stores; here the feature is enabled on its memory store.
                module.Configure<KeyValueFeature>(_ => { });
            });
            services.AddSingleton<ISystemClock>(_clock);
            services.AddScoped<ScheduledPipelinesTask>();
            services.AddScoped<PipelineRunRetentionTask>();
            services.AddScoped<PipelineRunSettingsTask>();
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class CapturedOut(TextWriter writer) : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => writer;
        }

        private sealed class FakeClock(DateTimeOffset now) : ISystemClock
        {
            public DateTimeOffset UtcNow { get; set; } = now;
        }

        [Step("Stomme.Tests", "Tests", "Captures the last result.")]
        public sealed class Capture : CodeStep
        {
            protected override void Execute(StepExecutionContext context) =>
                context.GetRequiredService<ResultSink>().Value = context.GetLastResult()?.ToString();
        }

        [Step("Stomme.Tests", "Tests", "Fails the run.")]
        public sealed class Boom : CodeStep
        {
            protected override void Execute(StepExecutionContext context) => throw new InvalidOperationException("boom");
        }

        // The engine's flowchart serializer keys its steps by id, so a step built in code needs one to be published.
        private static Flowchart Chain(IStep trigger, IStep next)
        {
            trigger.Id = "trigger";
            trigger.SetCanStartPipeline(true);
            next.Id = "next";
            return new Flowchart
            {
                Steps = { trigger, next },
                Connections = { new Connection(new Endpoint(trigger), new Endpoint(next)) },
            };
        }

        // A trigger, then two steps in a row.
        private static Flowchart Chain(IStep trigger, IStep next, IStep then)
        {
            var chart = Chain(trigger, next);
            then.Id = "then";
            chart.Steps.Add(then);
            chart.Connections.Add(new Connection(new Endpoint(next), new Endpoint(then)));
            return chart;
        }

        private async Task<string> PublishAsync(IServiceProvider services, string name, IStep root)
        {
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(root);
            definition.Name = name;
            var published = await publisher.PublishAsync(definition);
            Assert.True(published.Succeeded, string.Join("; ", published.ValidationErrors.Select(e => e.Message)));
            return definition.DefinitionId;
        }

        private async Task<long> RunsAsync(IServiceProvider services, string definitionId) =>
            await services.GetRequiredService<IPipelineInstanceStore>().CountAsync(new PipelineInstanceFilter { DefinitionId = definitionId });

        [Fact]
        public async Task A_schedule_is_indexed_at_publish_and_fires_once_per_due_occurrence()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var definitionId = await PublishAsync(services, "Every five minutes", Chain(new Schedule { Cron = new("*/5 * * * *") }, new WriteLine("scheduled run")));

            // Indexed under the trigger's type, with the normalised cron as its payload.
            var stored = Assert.Single(await services.GetRequiredService<ITriggerStore>().FindManyAsync(new TriggerFilter { Name = ScheduledPipelinesTask.StepTypeName }));
            Assert.Equal("*/5 * * * *", ScheduledPipelinesTask.ReadCron(stored));

            var task = services.GetRequiredService<ScheduledPipelinesTask>();
            // First sight anchors at 10:00:30 and fires nothing: publishing a schedule does not replay the past.
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(0, await RunsAsync(services, definitionId));
            // 10:04:00 - not yet.
            _clock.UtcNow = _clock.UtcNow.AddMinutes(3.5);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(0, await RunsAsync(services, definitionId));
            // 10:05:10 - due: one run, and a second tick in the same window fires nothing more.
            _clock.UtcNow = _clock.UtcNow.AddSeconds(70);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(1, await RunsAsync(services, definitionId));
            Assert.Contains("scheduled run", _console.ToString());
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(1, await RunsAsync(services, definitionId));
            // The host was down over 10:10 and 10:15: at 10:16 exactly ONE catch-up fire, not two.
            _clock.UtcNow = _clock.UtcNow.AddMinutes(11);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(2, await RunsAsync(services, definitionId));

            // Switched off, nothing fires however due.
            _options.Schedules.Enabled = false;
            _clock.UtcNow = _clock.UtcNow.AddMinutes(10);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(2, await RunsAsync(services, definitionId));
            _options.Schedules.Enabled = true;

            // The fire time reached the pipeline as the trigger's result.
            var withCapture = await PublishAsync(services, "Capture the fire time", Chain(new Schedule { Cron = new("* * * * *") }, new Capture()));
            await task.ExecuteAsync(CancellationToken.None);
            _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(1, await RunsAsync(services, withCapture));
            Assert.Equal(_clock.UtcNow.ToString("O"), _services.GetRequiredService<ResultSink>().Value);
        }

        // BL-229, n8n's trigger rules: a schedule of several cron lines is indexed under each, and each fires on its own time; the
        // lines are held to the rules at the save, the second one's problem named, and at most ten.
        [Fact]
        public async Task A_schedule_of_several_lines_fires_on_each_and_a_bad_line_is_refused()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var definitionId = await PublishAsync(services, "On the hour and the half", Chain(new Schedule { Cron = new("0 * * * *\n\n 30 * * * * \n0 * * * *") }, new WriteLine("ruled run")));
            var stored = await services.GetRequiredService<ITriggerStore>().FindManyAsync(new TriggerFilter { Name = ScheduledPipelinesTask.StepTypeName });
            Assert.Equal(new[] { "0 * * * *", "30 * * * *" }, stored.Select(ScheduledPipelinesTask.ReadCron).OrderBy(cron => cron, StringComparer.Ordinal)); // blank and twice-given lines once

            var task = services.GetRequiredService<ScheduledPipelinesTask>();
            await task.ExecuteAsync(CancellationToken.None); // 10:00:30 - anchors both
            _clock.UtcNow = _clock.UtcNow.AddMinutes(29.7); // 10:30:12 - the half-hour line
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(1, await RunsAsync(services, definitionId));
            _clock.UtcNow = _clock.UtcNow.AddMinutes(30); // 11:00:12 - the hour line
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(2, await RunsAsync(services, definitionId));

            Assert.Equal(new[] { "0 * * * *", "30 * * * *" }, Schedule.Lines("0  *  * * *\r\n30 * * * *\n"));
            Assert.Equal(new[] { "0 6 * * *" }, Schedule.Lines("0 6 * * *\n 0  6 * * * ")); // the same time twice is one line - counted once against the ten

            // The save holds every line, the second as much as the first.
            var graph = new PipelineGraph { Steps = { new PipelineGraphStep { Id = "when", Type = "Stomme.Schedule", Inputs = { ["Cron"] = "0 6 * * *\nnot a cron" } } } };
            Assert.StartsWith("'not a cron' is not a five-field cron expression", Assert.Throws<ArgumentException>(() =>
                PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>())).Message);
            Assert.Null(Schedule.TryParseAll("0 6 * * *\nnot a cron", out var error));
            Assert.StartsWith("'not a cron' is not a five-field cron expression", error);
            Assert.Null(Schedule.TryParseAll(string.Join("\n", Enumerable.Range(0, 11).Select(minute => minute + " * * * *")), out error));
            Assert.Equal("A schedule holds at most 10 cron lines - this one has 11.", error);
            var next = Schedule.Next(Schedule.TryParseAll("0 * * * *\n30 * * * *", out _)!, new DateTime(2026, 10, 10, 10, 0, 30, DateTimeKind.Utc), TimeZoneInfo.Utc, 3);
            Assert.Equal(new[] { "10:30", "11:00", "11:30" }, next.Select(moment => moment.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)));
        }

        // BL-229: a schedule read in a time zone fires when the zone's clock reaches it, daylight saving followed - '0 6 * * *' in
        // Stockholm in October (UTC+2) is 04:00 UTC - and the same cron in UTC does not fire then. A schedule with no zone hashes
        // as the stimulus did before zones existed, so every schedule published before keeps firing without a republish.
        [Fact]
        public async Task A_schedule_in_a_time_zone_fires_by_the_zones_clock_and_one_without_hashes_as_before()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            _clock.UtcNow = new DateTimeOffset(2026, 10, 6, 3, 0, 30, TimeSpan.Zero);
            var stockholm = await PublishAsync(services, "Six in Stockholm", Chain(new Schedule { Cron = new("0 6 * * *"), TimeZone = new("Europe/Stockholm") }, new WriteLine("stockholm run")));
            var utc = await PublishAsync(services, "Six in UTC", Chain(new Schedule { Cron = new("0 6 * * *"), TimeZone = new(" utc ") }, new WriteLine("utc run")));

            var stored = await services.GetRequiredService<ITriggerStore>().FindManyAsync(new TriggerFilter { Name = ScheduledPipelinesTask.StepTypeName });
            Assert.Equal(new[] { "Europe/Stockholm" }, stored.Select(ScheduledPipelinesTask.ReadTimeZone).Where(zone => zone is not null));
            Assert.Equal(2, stored.Count());

            var task = services.GetRequiredService<ScheduledPipelinesTask>();
            await task.ExecuteAsync(CancellationToken.None); // anchors both
            _clock.UtcNow = new DateTimeOffset(2026, 10, 6, 4, 0, 30, TimeSpan.Zero);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal((1L, 0L), (await RunsAsync(services, stockholm), await RunsAsync(services, utc)));
            _clock.UtcNow = new DateTimeOffset(2026, 10, 6, 6, 0, 30, TimeSpan.Zero);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal((1L, 1L), (await RunsAsync(services, stockholm), await RunsAsync(services, utc)));

            // The hash a UTC schedule is found by is the one it had before zones: its stimulus still serializes as the cron alone
            // (the engine's hash converter writes every property, a null one too - measured - so a zone property on it would have
            // moved every published schedule's hash).
            var hasher = services.GetRequiredService<IStimulusHasher>();
            Assert.Equal(hasher.Hash(ScheduledPipelinesTask.StepTypeName, new { Cron = "0 6 * * *" }), hasher.Hash(ScheduledPipelinesTask.StepTypeName, Schedule.Stimulus("0 6 * * *", "UTC")));
            Assert.IsType<ScheduleStimulus>(Schedule.Stimulus("0 6 * * *", null));
            Assert.NotEqual(hasher.Hash(ScheduledPipelinesTask.StepTypeName, Schedule.Stimulus("0 6 * * *", null)), hasher.Hash(ScheduledPipelinesTask.StepTypeName, Schedule.Stimulus("0 6 * * *", "Europe/Stockholm")));

            // A zone the server does not know is refused where the editor saves it.
            Assert.Null(Schedule.FindZone("Mars/Olympus_Mons", out var error));
            Assert.Contains("is not a time zone this server knows", error);
            Assert.Same(TimeZoneInfo.Utc, Schedule.FindZone("", out _));
        }

        // BL-229 - n8n's workflow timezone: a schedule with no zone of its own is read in its pipeline's - '0 6 * * *' in a
        // Stockholm pipeline fires at 04:00 UTC in October - and one with a zone of its own keeps it.
        [Fact]
        public async Task A_schedule_with_no_zone_of_its_own_is_read_in_its_pipelines()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            _clock.UtcNow = new DateTimeOffset(2026, 10, 6, 3, 0, 30, TimeSpan.Zero);
            var inherits = Chain(new Schedule { Cron = new("0 6 * * *") }, new WriteLine("pipeline zone run"));
            inherits.Metadata["designer"] = new Dictionary<string, object> { ["timeZone"] = "Europe/Stockholm" }; // as the editor's codec keeps it
            var own = Chain(new Schedule { Cron = new("0 6 * * *"), TimeZone = new("America/New_York") }, new WriteLine("own zone run"));
            own.Metadata["designer"] = new Dictionary<string, object> { ["timeZone"] = "Europe/Stockholm" };
            var zoned = await PublishAsync(services, "Six where the pipeline is", inherits);
            var kept = await PublishAsync(services, "Six in New York", own);

            var stored = await services.GetRequiredService<ITriggerStore>().FindManyAsync(new TriggerFilter { Name = ScheduledPipelinesTask.StepTypeName });
            Assert.Equal(new[] { "America/New_York", "Europe/Stockholm" }, stored.Select(ScheduledPipelinesTask.ReadTimeZone).OfType<string>().OrderBy(zone => zone));

            var task = services.GetRequiredService<ScheduledPipelinesTask>();
            await task.ExecuteAsync(CancellationToken.None); // anchors both
            _clock.UtcNow = new DateTimeOffset(2026, 10, 6, 4, 0, 30, TimeSpan.Zero);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal((1L, 0L), (await RunsAsync(services, zoned), await RunsAsync(services, kept)));
        }

        // 10.18.0 moved a schedule's last fire under the site's key (two sites with the same cron shared one row and neither
        // fired - EngineStoreConformanceTests). A fire kept under the old key still anchors the schedule, so an occurrence the
        // host was down over during the upgrade fires once, as any misfire does, instead of being re-anchored away.
        [Fact]
        public async Task A_last_fire_kept_before_the_key_carried_the_site_still_anchors_the_schedule()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var definitionId = await PublishAsync(services, "Every five minutes, upgraded", Chain(new Schedule { Cron = new("*/5 * * * *") }, new WriteLine("upgraded run")));
            var hash = services.GetRequiredService<IStimulusHasher>().Hash(ScheduledPipelinesTask.StepTypeName, Schedule.Stimulus("*/5 * * * *", null));
            await services.GetRequiredService<IKeyValueStore>().SaveAsync(new SerializedKeyValuePair
            {
                Key = ScheduledPipelinesTask.LastFiredKeyPrefix + hash,
                SerializedValue = _clock.UtcNow.ToString("O"),
            }, CancellationToken.None);

            // 10:05:10, first tick after the upgrade: due by the old anchor (10:00:30), so it fires - a first sight would not.
            _clock.UtcNow = _clock.UtcNow.AddMinutes(4).AddSeconds(40);
            var task = services.GetRequiredService<ScheduledPipelinesTask>();
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(1, await RunsAsync(services, definitionId));
            // From then on the site's own key holds it: the same window fires nothing more.
            Assert.NotNull(await services.GetRequiredService<IKeyValueStore>().FindAsync(new KeyValueFilter { Key = ScheduledPipelinesTask.LastFiredKey(services.GetRequiredService<ITenantAccessor>().TenantId, hash) }, CancellationToken.None));
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(1, await RunsAsync(services, definitionId));
        }

        // BL-229 - n8n's execution pruning: with Pipelines:Runs:KeepDays set, a finished run older than that goes, its log with
        // it; a recent one stays, and a run still waiting stays however old. Off (0, the default), nothing goes.
        // BL-229: n8n's "save successful executions" - a pipeline set not to keep its good runs has each deleted with its log once
        // finished for the grace; its failed run stays, and so does every run of a pipeline that keeps them.
        [Fact]
        public async Task A_pipeline_that_keeps_only_failed_runs_has_its_good_runs_deleted_after_a_grace()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var sender = services.GetRequiredService<IStimulusSender>();
            var lean = await PublishAsync(services, "Lean", Chain(new Webhook { WebhookName = new("lean") }, new WriteLine("a good run")));
            var leanFails = await PublishAsync(services, "Lean and failing", Chain(new Webhook { WebhookName = new("lean-fails") }, new Boom()));
            var keeps = await PublishAsync(services, "Keeps", Chain(new Webhook { WebhookName = new("keeps") }, new WriteLine("kept")));
            var store = services.GetRequiredService<IPipelineDefinitionStore>();
            foreach (var id in new[] { lean, leanFails })
            {
                var definition = (await store.FindAsync(new PipelineDefinitionFilter { DefinitionId = id, VersionOptions = VersionOptions.Published }))!;
                var body = JsonNode.Parse(definition.StringData!)!.AsObject();
                var metadata = body["metadata"] as JsonObject ?? new JsonObject();
                metadata["designer"] = new JsonObject { ["keepSuccessfulRuns"] = false };
                body["metadata"] = metadata;
                definition.StringData = body.ToJsonString();
                await store.SaveAsync(definition);
            }

            _clock.UtcNow = new DateTimeOffset(2026, 10, 7, 8, 0, 0, TimeSpan.Zero);
            foreach (var name in new[] { "lean", "lean-fails", "keeps" })
            {
                await sender.SendAsync("Stomme.Webhook", new WebhookStimulus(name));
            }

            var instances = services.GetRequiredService<IPipelineInstanceStore>();
            Assert.Equal(PipelineSubStatus.Faulted, Assert.Single(await instances.FindManyAsync(new PipelineInstanceFilter { DefinitionId = leanFails })).SubStatus);
            var goodRun = Assert.Single(await instances.FindManyAsync(new PipelineInstanceFilter { DefinitionId = lean }));
            var task = services.GetRequiredService<PipelineRunSettingsTask>();

            // Within the grace, the good run can still be opened.
            _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(1, await RunsAsync(services, lean));

            // After it, the good run goes with its log; the failed run and the other pipeline's run stay.
            _clock.UtcNow = _clock.UtcNow.AddMinutes(2);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal((0, 1, 1), (await RunsAsync(services, lean), await RunsAsync(services, leanFails), await RunsAsync(services, keeps)));
            Assert.Null(await services.GetRequiredService<IPipelineExecutionLogStore>().FindAsync(new PipelineExecutionLogRecordFilter { PipelineInstanceId = goodRun.Id }));
        }

        // BL-229 - n8n Enterprise's execution data redaction: a pipeline that keeps its steps' data only for a run that fails has a
        // good run's step records emptied once finished for the grace - the run and its log stay, a record says why; its failed run
        // keeps everything; "never" empties a failed run too; a pipeline that keeps it is left alone; and the mark makes the next
        // pass take only the runs finished since (an emptied record is not written again).
        [Fact]
        public async Task A_pipeline_that_keeps_no_step_data_has_its_runs_steps_emptied_after_a_grace_and_the_runs_kept()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var sender = services.GetRequiredService<IStimulusSender>();
            var failedOnly = await PublishAsync(services, "Failed only", Chain(new Webhook { WebhookName = new("failed-only") }, new WriteLine("secret 4111")));
            var failedOnlyFails = await PublishAsync(services, "Failed only, failing", Chain(new Webhook { WebhookName = new("failed-only-fails") }, new WriteLine("secret 4222"), new Boom()));
            var never = await PublishAsync(services, "Never", Chain(new Webhook { WebhookName = new("never") }, new WriteLine("secret 4333"), new Boom()));
            var keeps = await PublishAsync(services, "Keeps", Chain(new Webhook { WebhookName = new("keeps") }, new WriteLine("kept 4444")));
            var store = services.GetRequiredService<IPipelineDefinitionStore>();
            foreach (var (id, keep) in new[] { (failedOnly, "failed"), (failedOnlyFails, "failed"), (never, "never") })
            {
                var definition = (await store.FindAsync(new PipelineDefinitionFilter { DefinitionId = id, VersionOptions = VersionOptions.Published }))!;
                var body = JsonNode.Parse(definition.StringData!)!.AsObject();
                var metadata = body["metadata"] as JsonObject ?? new JsonObject();
                metadata["designer"] = new JsonObject { ["keepStepData"] = keep };
                body["metadata"] = metadata;
                definition.StringData = body.ToJsonString();
                await store.SaveAsync(definition);
            }

            _clock.UtcNow = new DateTimeOffset(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);
            foreach (var name in new[] { "failed-only", "failed-only-fails", "never", "keeps" })
            {
                await sender.SendAsync("Stomme.Webhook", new WebhookStimulus(name));
            }

            var instances = services.GetRequiredService<IPipelineInstanceStore>();
            var steps = services.GetRequiredService<IStepExecutionStore>();
            async Task<string> DataOf(string definitionId)
            {
                var run = Assert.Single(await instances.FindManyAsync(new PipelineInstanceFilter { DefinitionId = definitionId }));
                var records = (await steps.FindManyAsync(new StepExecutionRecordFilter { PipelineInstanceId = run.Id })).Where(record => record.StepType.EndsWith("WriteLine", StringComparison.Ordinal)).ToList();
                var record = Assert.Single(records);
                return record.Metadata?.ContainsKey(PipelineStepDataKeeping.NotKeptKey) == true
                    ? (record.StepState is null && record.Outputs is null && record.Payload is null ? "emptied" : "marked but holding data")
                    : (record.StepState is { Count: > 0 } ? "kept" : "no data");
            }

            // Two runs started with something: the one not to keep its data loses it with the rest, the one that keeps it keeps it.
            foreach (var id in new[] { never, failedOnlyFails })
            {
                var started = Assert.Single(await instances.FindManyAsync(new PipelineInstanceFilter { DefinitionId = id }));
                started.PipelineState.Properties[KeepRunInput.PropertyKey] = new Dictionary<string, object> { ["card"] = "4111" };
                await instances.SaveAsync(started);
            }

            async Task<string> InputOf(string definitionId)
            {
                var state = Assert.Single(await instances.FindManyAsync(new PipelineInstanceFilter { DefinitionId = definitionId })).PipelineState;
                return KeepRunInput.Of(state).ContainsKey("card") ? "kept" : KeepRunInput.WasEmptied(state) ? "emptied" : "none";
            }

            var task = services.GetRequiredService<PipelineRunSettingsTask>();

            // Within the grace, nothing is emptied.
            _clock.UtcNow = _clock.UtcNow.AddMinutes(1);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(("kept", "kept", "kept", "kept"), (await DataOf(failedOnly), await DataOf(failedOnlyFails), await DataOf(never), await DataOf(keeps)));
            Assert.Equal(("kept", "kept"), (await InputOf(never), await InputOf(failedOnlyFails)));

            // After it: the good run of "failed only" and the failed run of "never" are emptied; the rest keep theirs; every run stays.
            _clock.UtcNow = _clock.UtcNow.AddMinutes(2);
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(("emptied", "kept", "emptied", "kept"), (await DataOf(failedOnly), await DataOf(failedOnlyFails), await DataOf(never), await DataOf(keeps)));
            Assert.Equal(("emptied", "kept"), (await InputOf(never), await InputOf(failedOnlyFails)));
            Assert.Equal((1, 1, 1, 1), (await RunsAsync(services, failedOnly), await RunsAsync(services, failedOnlyFails), await RunsAsync(services, never), await RunsAsync(services, keeps)));
            var goodRun = Assert.Single(await instances.FindManyAsync(new PipelineInstanceFilter { DefinitionId = failedOnly }));
            Assert.NotNull(await services.GetRequiredService<IPipelineExecutionLogStore>().FindAsync(new PipelineExecutionLogRecordFilter { PipelineInstanceId = goodRun.Id }));

            // The mark: the next pass takes only runs finished since - a new run is emptied, and nothing is written twice.
            var tenant = services.GetRequiredService<ITenantAccessor>().TenantId;
            Assert.NotNull(await services.GetRequiredService<IKeyValueStore>().FindAsync(new KeyValueFilter { Key = PipelineRunSettingsTask.StepDataMarkKey(tenant, failedOnly) }, CancellationToken.None));
            await sender.SendAsync("Stomme.Webhook", new WebhookStimulus("failed-only"));
            _clock.UtcNow = _clock.UtcNow.AddMinutes(3);
            await task.ExecuteAsync(CancellationToken.None);
            var runs = await instances.FindManyAsync(new PipelineInstanceFilter { DefinitionId = failedOnly });
            Assert.Equal(2, runs.Count());
            foreach (var run in runs)
            {
                var record = (await steps.FindManyAsync(new StepExecutionRecordFilter { PipelineInstanceId = run.Id })).Single(record => record.StepType.EndsWith("WriteLine", StringComparison.Ordinal));
                Assert.Null(record.StepState);
            }
        }

        [Fact]
        public async Task Finished_runs_older_than_the_kept_days_are_deleted_with_their_log_and_nothing_else()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var sender = services.GetRequiredService<IStimulusSender>();
            var report = await PublishAsync(services, "Old and new", Chain(new Webhook { WebhookName = new("report") }, new WriteLine("a run")));
            var waits = await PublishAsync(services, "Waits", Chain(new Webhook { WebhookName = new("waits") }, new Stomme.Engine.Pipelines.Runtime.Steps.Event("never-raised")));

            _clock.UtcNow = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
            await sender.SendAsync("Stomme.Webhook", new WebhookStimulus("report"));
            await sender.SendAsync("Stomme.Webhook", new WebhookStimulus("waits"));
            _clock.UtcNow = new DateTimeOffset(2026, 10, 5, 8, 0, 0, TimeSpan.Zero);
            await sender.SendAsync("Stomme.Webhook", new WebhookStimulus("report"));

            var store = services.GetRequiredService<IPipelineInstanceStore>();
            var runs = (await store.FindManyAsync(new PipelineInstanceFilter { DefinitionId = report })).OrderBy(run => run.CreatedAt).ToList();
            Assert.Equal(2, runs.Count);
            var waiting = Assert.Single(await store.FindManyAsync(new PipelineInstanceFilter { DefinitionId = waits }));
            Assert.Equal(PipelineStatus.Running, waiting.Status);
            var logs = services.GetRequiredService<IPipelineExecutionLogStore>();
            Assert.NotNull(await logs.FindAsync(new PipelineExecutionLogRecordFilter { PipelineInstanceId = runs[0].Id }));

            // Off: nothing goes.
            _clock.UtcNow = new DateTimeOffset(2026, 10, 6, 8, 0, 0, TimeSpan.Zero);
            var task = services.GetRequiredService<PipelineRunRetentionTask>();
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(2, await RunsAsync(services, report));

            // Thirty days: the September run goes with its log; October's stays, and so does the run still waiting.
            _options.Runs.KeepDays = 30;
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(runs[1].Id, Assert.Single(await store.FindManyAsync(new PipelineInstanceFilter { DefinitionId = report })).Id);
            Assert.Null(await logs.FindAsync(new PipelineExecutionLogRecordFilter { PipelineInstanceId = runs[0].Id }));
            Assert.Equal(1, await RunsAsync(services, waits));
            _options.Runs.KeepDays = 0;
        }

        [Fact]
        public async Task A_webhook_stimulus_starts_the_pipeline_with_the_body_as_the_result_and_only_under_its_name()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var definitionId = await PublishAsync(services, "Orders webhook", Chain(new Webhook { WebhookName = new("Orders") }, new Capture()));
            Assert.Equal("orders", Assert.IsType<WebhookStimulus>(Assert.Single(await services.GetRequiredService<ITriggerStore>().FindManyAsync(new TriggerFilter { Name = "Stomme.Webhook" })).Payload).Name);

            var sender = services.GetRequiredService<IStimulusSender>();
            var other = await sender.SendAsync("Stomme.Webhook", new WebhookStimulus("invoices"));
            Assert.Empty(other.PipelineInstanceResponses);
            Assert.Equal(0, await RunsAsync(services, definitionId));

            var fired = await sender.SendAsync("Stomme.Webhook", new WebhookStimulus("orders"), new StimulusMetadata
            {
                CorrelationId = "order-42",
                Input = new Dictionary<string, object> { [Webhook.PayloadInputKey] = "{\"order\":42}", [Webhook.ContentTypeInputKey] = "application/json", [Webhook.NameInputKey] = "orders" },
            });
            Assert.Single(fired.PipelineInstanceResponses);
            Assert.Equal(1, await RunsAsync(services, definitionId));
            var instance = Assert.Single(await services.GetRequiredService<IPipelineInstanceStore>().FindManyAsync(new PipelineInstanceFilter { DefinitionId = definitionId }));
            Assert.True(instance.SubStatus == PipelineSubStatus.Finished, $"{instance.Status}/{instance.SubStatus}: {string.Join(" | ", instance.PipelineState.Incidents.Select(i => i.StepType + ": " + i.Message))}");
            Assert.Equal("{\"order\":42}", _services.GetRequiredService<ResultSink>().Value);
            Assert.Equal("order-42", instance.CorrelationId);

            // A form-encoded body is its fields, as a JSON object - a field given twice a list (found on UAT: none could be read).
            await sender.SendAsync("Stomme.Webhook", new WebhookStimulus("orders"), new StimulusMetadata
            {
                CorrelationId = "order-43",
                Input = new Dictionary<string, object> { [Webhook.PayloadInputKey] = "order=43&tag=a&tag=b&note=caf%C3%A9+au+lait", [Webhook.ContentTypeInputKey] = "application/x-www-form-urlencoded; charset=utf-8", [Webhook.NameInputKey] = "orders" },
            });
            Assert.Equal(2, await RunsAsync(services, definitionId));
            Assert.Equal("{\"order\":\"43\",\"tag\":[\"a\",\"b\"],\"note\":\"caf\\u00E9 au lait\"}", _services.GetRequiredService<ResultSink>().Value);
            Assert.Equal("café au lait", System.Text.Json.JsonDocument.Parse(_services.GetRequiredService<ResultSink>().Value!).RootElement.GetProperty("note").GetString());
        }

        [Fact]
        public void The_trigger_inputs_are_validated_where_the_editor_saves_them()
        {
            Assert.NotNull(Schedule.TryParse("0 6 * * 1-5", out var error));
            Assert.Null(error);
            Assert.NotNull(Schedule.TryParse("*/15 * * * *", out _));
            Assert.Null(Schedule.TryParse("every morning", out error));
            Assert.Contains("not a five-field cron expression", error);
            Assert.Null(Schedule.TryParse("0 0 0 * * *", out error));
            Assert.True(Webhook.IsValidName("orders.created-v2_1"));
            Assert.False(Webhook.IsValidName("orders/created"));
            Assert.False(Webhook.IsValidName(""));
            Assert.False(Webhook.IsValidName(new string('a', 101)));
        }

        [Fact]
        public void The_webhook_signature_is_the_hmac_of_the_body_and_verifies_in_constant_time()
        {
            var body = System.Text.Encoding.UTF8.GetBytes("{\"order\":42}");
            var signature = PipelineWebhooks.ComputeSignature("s3cret", body);
            Assert.StartsWith("sha256=", signature);
            Assert.True(PipelineWebhooks.VerifySignature("s3cret", body, signature));
            Assert.True(PipelineWebhooks.VerifySignature("s3cret", body, signature[7..].ToUpperInvariant()));
            Assert.False(PipelineWebhooks.VerifySignature("s3cret", body, "sha256=" + new string('0', 64)));
            Assert.False(PipelineWebhooks.VerifySignature("other", body, signature));
            Assert.False(PipelineWebhooks.VerifySignature(null, body, signature));
            Assert.False(PipelineWebhooks.VerifySignature("s3cret", body, null));
        }
    }
}
