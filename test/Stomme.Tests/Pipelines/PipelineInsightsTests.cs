#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Features;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.State;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Steps.Flowchart.Steps;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - n8n's Insights: each pipeline's runs in a window counted - failed, cancelled, not ended - with the failure rate of
    /// those that ended and the MEDIAN time they took (a run waiting days for a decision would make an average say nothing); and
    /// the window read through the engine's stores, its runs and no older one.
    /// </summary>
    public sealed class PipelineInsightsTests : IAsyncLifetime
    {
        private static readonly DateTime T0 = new(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);

        private static PipelineRunView Run(string id, string definitionId, string name, string subStatus, int startedMinute, double? tookSeconds) => new()
        {
            Id = id,
            DefinitionId = definitionId,
            PipelineName = name,
            Status = tookSeconds is null ? "Running" : "Finished",
            SubStatus = subStatus,
            CreatedOnUtc = T0.AddMinutes(startedMinute),
            FinishedOnUtc = tookSeconds is null ? null : T0.AddMinutes(startedMinute).AddSeconds(tookSeconds.Value),
        };

        [Fact]
        public void Each_pipelines_runs_are_counted_with_the_median_and_failure_rate_of_those_that_ended()
        {
            var runs = new[]
            {
                Run("a1", "def-a", "Nightly", "Finished", 0, 1),
                Run("a2", "def-a", "Nightly", "Finished", 1, 3),
                Run("a3", "def-a", "Nightly", "Faulted", 2, 2),
                Run("a4", "def-a", "Nightly", "Faulted", 5, 10),
                Run("a5", "def-a", "Nightly", "Suspended", 6, null),
                Run("a6", "def-a", "Nightly", "Cancelled", 7, 4),
                Run("b1", "def-b", "Alerts", "Finished", 3, 0.5),
            };

            var insights = PipelineInsights.Of(runs, 7, T0.AddDays(-7), capped: false);

            Assert.Equal((7, false), (insights.Days, insights.Capped));
            Assert.Equal(new[] { "def-a", "def-b" }, insights.Pipelines.Select(pipeline => pipeline.DefinitionId)); // the most runs first
            var nightly = insights.Pipelines[0];
            Assert.Equal(("Nightly", 6, 2, 1, 1), (nightly.PipelineName, nightly.Runs, nightly.Failed, nightly.Cancelled, nightly.Unfinished));
            Assert.Equal(3000, nightly.MedianMilliseconds); // of 1, 2, 3, 4 and 10 seconds; the run still waiting has no time
            Assert.Equal(0.4, nightly.FailureRate!.Value, 6); // 2 of the 5 that ended
            Assert.Equal(("a4", T0.AddMinutes(5)), (nightly.LastFailedRunId, nightly.LastFailedOnUtc));
            var alerts = insights.Pipelines[1];
            Assert.Equal((1, 0, 500L, 0.0), (alerts.Runs, alerts.Failed, alerts.MedianMilliseconds!.Value, alerts.FailureRate!.Value));
            Assert.Null(alerts.LastFailedRunId);

            Assert.Equal((7, 2, 1, 1), (insights.Total.Runs, insights.Total.Failed, insights.Total.Cancelled, insights.Total.Unfinished));
            Assert.Equal(2500, insights.Total.MedianMilliseconds); // an even count: the middle two
            Assert.Equal(2.0 / 6, insights.Total.FailureRate!.Value, 6);

            var none = PipelineInsights.Of(Array.Empty<PipelineRunView>(), 1, T0, capped: false);
            Assert.Equal((0, (long?)null, (double?)null), (none.Total.Runs, none.Total.MedianMilliseconds, none.Total.FailureRate));
            Assert.Empty(none.Pipelines);
        }

        // n8n's time saved: the minutes a pipeline says one run that finishes well saves, times its runs that did - a failed,
        // cancelled or unfinished run saved nothing - added up over the pipelines that say it; none saying, none said.
        [Fact]
        public void Time_saved_is_each_pipelines_minutes_per_run_times_the_runs_that_finished_well()
        {
            var runs = new[]
            {
                Run("a1", "def-a", "Nightly", "Finished", 0, 1),
                Run("a2", "def-a", "Nightly", "Finished", 1, 3),
                Run("a3", "def-a", "Nightly", "Faulted", 2, 2),
                Run("a4", "def-a", "Nightly", "Suspended", 3, null),
                Run("b1", "def-b", "Alerts", "Finished", 3, 0.5),
                Run("b2", "def-b", "Alerts", "Cancelled", 4, 1),
                Run("c1", "def-c", "Silent", "Finished", 5, 1),
            };

            var insights = PipelineInsights.Of(runs, 7, T0.AddDays(-7), capped: false, new System.Collections.Generic.Dictionary<string, int> { ["def-a"] = 15, ["def-b"] = 90, ["def-x"] = 5 });
            var nightly = insights.Pipelines.Single(pipeline => pipeline.DefinitionId == "def-a");
            Assert.Equal((15, 30L), (nightly.MinutesSavedPerRun!.Value, nightly.MinutesSaved!.Value));
            var alerts = insights.Pipelines.Single(pipeline => pipeline.DefinitionId == "def-b");
            Assert.Equal((90, 90L), (alerts.MinutesSavedPerRun!.Value, alerts.MinutesSaved!.Value));
            var silent = insights.Pipelines.Single(pipeline => pipeline.DefinitionId == "def-c");
            Assert.Equal(((int?)null, (long?)null), (silent.MinutesSavedPerRun, silent.MinutesSaved)); // it says nothing
            Assert.Equal(120L, insights.Total.MinutesSaved);

            Assert.Null(PipelineInsights.Of(runs, 7, T0.AddDays(-7), capped: false).Total.MinutesSaved);
            Assert.Equal(((int?)null, (int?)null, 1, PipelineInsights.MaxMinutesSavedPerRun), (PipelineInsights.MinutesSaved(0), PipelineInsights.MinutesSaved(null), PipelineInsights.MinutesSaved(1), PipelineInsights.MinutesSaved(int.MaxValue)));
        }

        // BL-229 - each step's figures from its passes: how many, how many failed (faulted, or failed and gone on down its Failed
        // path), the median time of those that ended; a pass naming no step is none of them.
        [Fact]
        public void Each_steps_passes_are_counted_with_their_failures_and_median_time()
        {
            PipelineStepRunView Pass(string step, string status, double? milliseconds, string? handled = null) => new()
            {
                StepId = step,
                StepName = step == "a" && status == "Faulted" ? "Fetch orders" : null!,
                Status = status,
                StartedAtUtc = T0,
                CompletedAtUtc = milliseconds is null ? null : T0.AddMilliseconds(milliseconds.Value),
                HandledFailure = handled!,
            };

            var steps = PipelineInsights.OfSteps(new[]
            {
                Pass("a", "Completed", 100), Pass("a", "Completed", 300), Pass("a", "Faulted", null), Pass("a", "Completed", 200, PipelineStepRunView.TookFailedPath),
                Pass("b", "Completed", 50),
                Pass("", "Completed", 10),
            });

            var a = steps.Single(step => step.StepId == "a");
            Assert.Equal((4, 2, 200L), (a.Passes, a.Failed, a.MedianMilliseconds!.Value)); // the faulted pass has no end: of 100, 200 and 300
            Assert.Equal("Fetch orders", a.StepName); // as a pass recorded it, for whoever reads the figures without the canvas
            Assert.Equal("4× · 2 failed · 200 ms", PipelineInsights.Describe(a));
            Assert.Equal("1× · 50 ms", PipelineInsights.Describe(steps.Single(step => step.StepId == "b")));
            Assert.Equal(2, steps.Count);
        }

        [Theory]
        [InlineData(45, "45 min")]
        [InlineData(60, "1 h")]
        [InlineData(125, "2 h 5 min")]
        [InlineData(6000, "100 h")]
        public void Time_saved_is_said_in_minutes_then_hours(long minutes, string said) =>
            Assert.Equal(said, PipelineInsights.Saved(minutes));

        [Theory]
        [InlineData(1, 1)]
        [InlineData(7, 7)]
        [InlineData(30, 30)]
        [InlineData(0, 7)]
        [InlineData(14, 7)]
        [InlineData(-30, 7)]
        public void A_window_is_a_day_a_week_or_a_month(int asked, int window) =>
            Assert.Equal(window, PipelineInsights.Window(asked));

        [Theory]
        [InlineData(0.4, "40 %")]
        [InlineData(0.125, "12.5 %")]
        [InlineData(0.0, "0 %")]
        [InlineData(1.0, "100 %")]
        [InlineData(null, "—")]
        public void A_rate_is_said_as_a_percentage(double? rate, string said) =>
            Assert.Equal(said, PipelineInsights.Percent(rate));

        private ServiceProvider _services = default!;
        private readonly FakeClock _clock = new(new DateTimeOffset(T0));

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddEngine(module =>
            {
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<KeyValueFeature>(_ => { });
            });
            services.AddSingleton<ISystemClock>(_clock);
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class FakeClock(DateTimeOffset now) : ISystemClock
        {
            public DateTimeOffset UtcNow { get; set; } = now;
        }

        private static PipelineInstance Instance(string definitionId, PipelineSubStatus subStatus, DateTimeOffset created, TimeSpan? took)
        {
            var id = Guid.NewGuid().ToString("N");
            return new PipelineInstance
            {
                Id = id,
                DefinitionId = definitionId,
                DefinitionVersionId = definitionId + "-1",
                Version = 1,
                Status = took is null ? PipelineStatus.Running : PipelineStatus.Finished,
                SubStatus = subStatus,
                Name = "run " + id,
                CorrelationId = "corr " + id,
                CreatedAt = created,
                UpdatedAt = created,
                FinishedAt = took is null ? null : created + took.Value,
                PipelineState = new PipelineState { Id = id, DefinitionId = definitionId },
            };
        }

        [Fact]
        public async Task The_window_is_read_through_the_engines_stores_its_runs_and_no_older_one()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(new Flowchart { Id = "flow", Steps = { new WriteLine("x") { Id = "x" } } });
            definition.Name = "Nightly";
            Assert.True((await publisher.PublishAsync(definition)).Succeeded);

            // The published body says a run that finishes well saves 15 minutes - as the editor's codec writes it.
            var definitions = services.GetRequiredService<IPipelineDefinitionStore>();
            var published = (await definitions.FindAsync(new Stomme.Engine.Pipelines.Management.Filters.PipelineDefinitionFilter { DefinitionId = definition.DefinitionId, VersionOptions = Stomme.Engine.Common.Models.VersionOptions.Published }))!;
            var body = System.Text.Json.Nodes.JsonNode.Parse(published.StringData!)!.AsObject();
            body["metadata"] = new System.Text.Json.Nodes.JsonObject { ["designer"] = new System.Text.Json.Nodes.JsonObject { ["minutesSaved"] = 15 } };
            published.StringData = body.ToJsonString();
            await definitions.SaveAsync(published);

            var store = services.GetRequiredService<IPipelineInstanceStore>();
            var now = _clock.UtcNow;
            await store.AddAsync(Instance(definition.DefinitionId, PipelineSubStatus.Finished, now.AddHours(-1), TimeSpan.FromSeconds(2)));
            await store.AddAsync(Instance(definition.DefinitionId, PipelineSubStatus.Faulted, now.AddDays(-2), TimeSpan.FromSeconds(4)));
            await store.AddAsync(Instance(definition.DefinitionId, PipelineSubStatus.Finished, now.AddDays(-8), TimeSpan.FromSeconds(60)));
            var handler = new GetInsightsQueryHandler(store, services.GetRequiredService<IPipelineDefinitionStore>(), _clock);

            var week = await handler.Handle(new GetInsightsQuery(7), CancellationToken.None);
            var nightly = Assert.Single(week.Pipelines);
            Assert.Equal(("Nightly", 2, 1, 3000L), (nightly.PipelineName, nightly.Runs, nightly.Failed, nightly.MedianMilliseconds!.Value));
            Assert.Equal((7, now.AddDays(-7).UtcDateTime), (week.Days, week.SinceUtc));
            Assert.Equal((15, 15L, 15L), (nightly.MinutesSavedPerRun!.Value, nightly.MinutesSaved!.Value, week.Total.MinutesSaved!.Value)); // the one that finished well

            var day = await handler.Handle(new GetInsightsQuery(1), CancellationToken.None);
            Assert.Equal((1, 0), (day.Total.Runs, day.Total.Failed));

            var month = await handler.Handle(new GetInsightsQuery(30), CancellationToken.None);
            Assert.Equal((3, 30L), (month.Total.Runs, month.Total.MinutesSaved!.Value));

            // A window not offered is a week.
            Assert.Equal(7, (await handler.Handle(new GetInsightsQuery(365), CancellationToken.None)).Days);
        }

        // BL-229 - what runs next, off the published bodies with the schedules task's rules: a schedule that starts its pipeline, its
        // cron in its zone (06:00 in Stockholm is 04:00 UTC in October), the soonest first; an every-minute one counted to the limit;
        // one switched off, or one something leads to, not listed; nothing listed past the window.
        [Fact]
        public async Task What_runs_next_is_read_off_the_published_pipelines_schedules()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(new Flowchart { Id = "flow", Steps = { new WriteLine("x") { Id = "x" } } });
            definition.Name = "Morning";
            Assert.True((await publisher.PublishAsync(definition)).Succeeded);

            PipelineGraphStep Schedule(string id, string cron, string? zone = null, bool off = false) => new()
            {
                Id = id,
                Type = "Stomme.Schedule",
                Name = id,
                Disabled = off,
                Inputs = zone is null
                    ? new System.Collections.Generic.Dictionary<string, string> { ["Cron"] = cron }
                    : new System.Collections.Generic.Dictionary<string, string> { ["Cron"] = cron, ["TimeZone"] = zone },
            };
            var graph = new PipelineGraph
            {
                Steps = { Schedule("morning", "0 6 * * *", "Europe/Stockholm"), Schedule("minutely", "* * * * *"), Schedule("twice", "0 12 * * *" + System.Environment.NewLine + "0 18 * * *"), Schedule("off", "0 7 * * *", off: true), Schedule("led", "0 8 * * *"), new PipelineGraphStep { Id = "say", Type = "Stomme.LogMessage", Inputs = { ["Message"] = "hi" } } },
                Connections = { new PipelineGraphConnection { SourceId = "morning", TargetId = "say" }, new PipelineGraphConnection { SourceId = "say", TargetId = "led" } },
            };
            var definitions = services.GetRequiredService<IPipelineDefinitionStore>();
            var published = (await definitions.FindAsync(new Stomme.Engine.Pipelines.Management.Filters.PipelineDefinitionFilter { DefinitionId = definition.DefinitionId, VersionOptions = Stomme.Engine.Common.Models.VersionOptions.Published }))!;
            published.StringData = PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
            await definitions.SaveAsync(published);

            var options = Microsoft.Extensions.Options.Options.Create(new Stomme.Modules.Pipelines.Triggers.PipelineTriggerOptions());
            options.Value.Schedules.Enabled = true;
            var handler = new ListUpcomingRunsQueryHandler(definitions, options, new FixedTime(new DateTimeOffset(T0)));

            var day = await handler.Handle(new ListUpcomingRunsQuery(24), CancellationToken.None);
            Assert.Equal((24, true), (day.Hours, day.Enabled));
            Assert.Equal(new[] { "minutely", "twice", "morning" }, day.Schedules.Select(schedule => schedule.StepId)); // the soonest first
            var minutely = day.Schedules[0];
            Assert.Equal((T0.AddMinutes(1), PipelineUpcoming.MaxCounted, PipelineUpcoming.Shown), (minutely.NextRunsUtc[0], minutely.RunsInWindow, minutely.NextRunsUtc.Count));
            // BL-229, n8n's trigger rules: a schedule's lines merged - noon and six today, the lines said as one.
            var twice = day.Schedules[1];
            Assert.Equal(("0 12 * * *; 0 18 * * *", 2), (twice.Cron, twice.RunsInWindow));
            Assert.Equal(new[] { T0.AddHours(3), T0.AddHours(9) }, twice.NextRunsUtc);
            var morning = day.Schedules[2];
            Assert.Equal(("Morning", "Europe/Stockholm", 1), (morning.PipelineName, morning.TimeZone, morning.RunsInWindow));
            Assert.Equal(new DateTime(2026, 10, 7, 4, 0, 0, DateTimeKind.Utc), morning.NextRunsUtc.Single());

            var week = await handler.Handle(new ListUpcomingRunsQuery(168), CancellationToken.None);
            Assert.Equal(7, week.Schedules.Single(schedule => schedule.StepId == "morning").RunsInWindow);
            Assert.Equal(24, (await handler.Handle(new ListUpcomingRunsQuery(5), CancellationToken.None)).Hours); // a window not offered is a day
        }

        private sealed class FixedTime(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }

        // BL-229 - each step's figures read through the engine's stores: the window's runs of the one pipeline, their passes' states
        // and times; the engine's own wrappers left out, an older run not counted.
        [Fact]
        public async Task Each_steps_figures_are_read_through_the_engines_stores()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(new Flowchart { Id = "flow", Steps = { new WriteLine("x") { Id = "x" } } });
            Assert.True((await publisher.PublishAsync(definition)).Succeeded);

            var store = services.GetRequiredService<IPipelineInstanceStore>();
            var steps = services.GetRequiredService<Stomme.Engine.Pipelines.Runtime.IStepExecutionStore>();
            var now = _clock.UtcNow;
            async Task RunAsync(DateTimeOffset at, StepStatus status, double milliseconds)
            {
                var run = Instance(definition.DefinitionId, status == StepStatus.Faulted ? PipelineSubStatus.Faulted : PipelineSubStatus.Finished, at, TimeSpan.FromSeconds(1));
                await store.AddAsync(run);
                foreach (var (id, type) in new[] { ("flow", "Stomme.Flowchart"), ("x", "Stomme.WriteLine") })
                {
                    await steps.SaveAsync(new Stomme.Engine.Pipelines.Runtime.Entities.StepExecutionRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        PipelineInstanceId = run.Id,
                        StepId = id,
                        StepNodeId = id,
                        StepType = type,
                        Status = id == "x" ? status : StepStatus.Completed,
                        StartedAt = at,
                        CompletedAt = status == StepStatus.Faulted && id == "x" ? null : at.AddMilliseconds(milliseconds),
                    });
                }
            }

            await RunAsync(now.AddHours(-1), StepStatus.Completed, 120);
            await RunAsync(now.AddHours(-2), StepStatus.Faulted, 0);
            await RunAsync(now.AddDays(-8), StepStatus.Completed, 900); // outside the week
            var handler = new GetStepInsightsQueryHandler(store, steps, _clock);

            var week = await handler.Handle(new GetStepInsightsQuery(definition.DefinitionId, 7), CancellationToken.None);
            Assert.Equal((7, 2, false), (week.Days, week.Runs, week.Capped));
            var step = Assert.Single(week.Steps); // the flowchart wrapping it is not a step of the canvas
            Assert.Equal(("x", 2, 1, 120L), (step.StepId, step.Passes, step.Failed, step.MedianMilliseconds!.Value));
            Assert.Equal((3, 3), ((await handler.Handle(new GetStepInsightsQuery(definition.DefinitionId, 30), CancellationToken.None)).Runs, (await handler.Handle(new GetStepInsightsQuery(definition.DefinitionId, 30), CancellationToken.None)).Steps.Single().Passes));
        }
    }
}
