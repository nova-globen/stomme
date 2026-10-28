#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Reliability;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using PipelineGraph = Stomme.Models.Pipelines.PipelineGraph;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-229 - a run stopped where it is, by the ENGINE through the module's run-control step middleware: a run a person asks to
    /// stop while it works, and one busy longer than its pipeline's time limit (n8n's "Timeout Workflow"), is cancelled before its
    /// next step - that step, and every one after it, does not start, and the run's log says why. The time a run waits is not
    /// counted: each burst of work has the whole limit.
    /// </summary>
    public sealed class PipelineRunControlTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly StringWriter _console = new();
        private readonly Clock _clock = new() { UtcNow = new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero) };
        private readonly PipelineRunStops _stops = new();

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            var console = _console;
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new CapturedOut(console));
                module.AddStepsFrom<PipelineStepsMarker>();
                module.AddStep<PressStop>();
                module.AddStep<TakeLong>();
                module.AddStep<WaitHere>();
                module.UseStepReliability();
                RunControlMiddleware.UseRunControl(module);
            });
            services.AddSingleton<ISystemClock>(_clock);
            services.AddSingleton(_clock);
            services.AddSingleton(_stops);
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

        public sealed class Clock : ISystemClock
        {
            public DateTimeOffset UtcNow { get; set; }
        }

        /// <summary>A person pressing Stop while this step works: the run is asked to stop, and the step itself finishes.</summary>
        [Step("Stomme.Tests", "Tests", "Asks for its own run to stop.")]
        public sealed class PressStop : CodeStep
        {
            protected override void Execute(StepExecutionContext context) =>
                context.GetRequiredService<PipelineRunStops>().Ask(context.PipelineExecutionContext.Id, context.GetRequiredService<Clock>().UtcNow);
        }

        /// <summary>A step that takes its time: the clock moves on by the minutes it says.</summary>
        [Step("Stomme.Tests", "Tests", "Takes a while.")]
        public sealed class TakeLong : CodeStep
        {
            [Input]
            public Input<int> Minutes { get; set; } = new(0);

            protected override void Execute(StepExecutionContext context)
            {
                var clock = context.GetRequiredService<Clock>();
                clock.UtcNow = clock.UtcNow.AddMinutes(Minutes.Get(context));
            }
        }

        /// <summary>Waits, stored, until it is resumed - a run's burst of work ends here.</summary>
        [Step("Stomme.Tests", "Tests", "Waits to be resumed.")]
        public sealed class WaitHere : Step
        {
            protected override ValueTask ExecuteAsync(StepExecutionContext context)
            {
                context.CreateBookmark(ResumedAsync);
                return default;
            }

            private static async ValueTask ResumedAsync(StepExecutionContext context) => await context.CompleteStepAsync();
        }

        // A first step, then "after" (a line written), then "last": what a stop before the second must keep from running.
        private static PipelineGraph Graph(string firstType, int? timeoutMinutes, string minutes = "0") => new()
        {
            TimeoutMinutes = timeoutMinutes,
            Steps =
            {
                new PipelineGraphStep { Id = "first", Type = firstType, Inputs = firstType == "Stomme.Tests.TakeLong" ? new() { ["Minutes"] = minutes } : new() },
                new PipelineGraphStep { Id = "after", Type = "Stomme.WriteLine", Inputs = { ["Text"] = "after the first step" } },
                new PipelineGraphStep { Id = "last", Type = "Stomme.WriteLine", Inputs = { ["Text"] = "the last step" } },
            },
            Connections = { new PipelineGraphConnection { SourceId = "first", TargetId = "after" }, new PipelineGraphConnection { SourceId = "after", TargetId = "last" } },
        };

        private async Task<RunPipelineResult> RunAsync(PipelineGraph graph)
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var body = PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
            return await services.GetRequiredService<IPipelineRunner>().RunAsync(services.GetRequiredService<IStepSerializer>().Deserialize(body));
        }

        private static string[] Logged(RunPipelineResult result) =>
            result.Journal.PipelineExecutionLogEntries.Select(entry => entry.EventName + ": " + entry.Message).ToArray();

        [Fact]
        public async Task A_run_asked_to_stop_while_it_works_stops_before_its_next_step()
        {
            var result = await RunAsync(Graph("Stomme.Tests.PressStop", timeoutMinutes: null));

            Assert.Equal((PipelineStatus.Finished, PipelineSubStatus.Cancelled), (result.PipelineState.Status, result.PipelineState.SubStatus));
            Assert.DoesNotContain("after the first step", _console.ToString());
            Assert.DoesNotContain("the last step", _console.ToString());
            Assert.Contains("Stopped: The run was stopped while it worked, by a person.", Logged(result));
            Assert.False(_stops.Take(result.PipelineState.Id)); // the ask was taken by the stop, not left behind
        }

        [Fact]
        public async Task A_run_busy_longer_than_its_pipelines_time_limit_is_stopped_and_one_within_it_is_not()
        {
            var stopped = await RunAsync(Graph("Stomme.Tests.TakeLong", timeoutMinutes: 1, minutes: "2"));
            Assert.Equal(PipelineSubStatus.Cancelled, stopped.PipelineState.SubStatus);
            Assert.DoesNotContain("after the first step", _console.ToString());
            Assert.Contains("Stopped: The run was stopped: it was busy longer than 1 minute in one go - the pipeline's time limit.", Logged(stopped));

            // Within it, or with none: the run goes to its end.
            var within = await RunAsync(Graph("Stomme.Tests.TakeLong", timeoutMinutes: 5, minutes: "2"));
            Assert.Equal(PipelineSubStatus.Finished, within.PipelineState.SubStatus);
            var unlimited = await RunAsync(Graph("Stomme.Tests.TakeLong", timeoutMinutes: null, minutes: "600"));
            Assert.Equal(PipelineSubStatus.Finished, unlimited.PipelineState.SubStatus);
            Assert.Contains("the last step", _console.ToString());
        }

        // Busy two minutes, waiting an hour, busy two minutes again, under a three-minute limit: never three in one go, so the run
        // goes to its end - counted from the run's start, it would have been stopped after its wait.
        [Fact]
        public async Task The_time_a_run_waits_is_not_counted_against_its_limit()
        {
            var graph = new PipelineGraph
            {
                TimeoutMinutes = 3,
                Steps =
                {
                    new PipelineGraphStep { Id = "before", Type = "Stomme.Tests.TakeLong", Inputs = { ["Minutes"] = "2" } },
                    new PipelineGraphStep { Id = "wait", Type = "Stomme.Tests.WaitHere" },
                    new PipelineGraphStep { Id = "again", Type = "Stomme.Tests.TakeLong", Inputs = { ["Minutes"] = "2" } },
                    new PipelineGraphStep { Id = "after", Type = "Stomme.WriteLine", Inputs = { ["Text"] = "after the wait" } },
                },
                Connections =
                {
                    new PipelineGraphConnection { SourceId = "before", TargetId = "wait" },
                    new PipelineGraphConnection { SourceId = "wait", TargetId = "again" },
                    new PipelineGraphConnection { SourceId = "again", TargetId = "after" },
                },
            };
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var body = PipelineGraphCodec.ToBody(graph, null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
            var pipeline = Stomme.Engine.Pipelines.Steps.Pipeline.FromStep(services.GetRequiredService<IStepSerializer>().Deserialize(body));
            var runner = services.GetRequiredService<IPipelineRunner>();

            var waiting = await runner.RunAsync(pipeline);
            Assert.Equal(PipelineSubStatus.Suspended, waiting.PipelineState.SubStatus);

            _clock.UtcNow = _clock.UtcNow.AddHours(1);
            var resumed = await runner.RunAsync(pipeline, waiting.PipelineState, new RunPipelineOptions { BookmarkId = waiting.PipelineState.Bookmarks.Single().Id });
            Assert.Equal(PipelineSubStatus.Finished, resumed.PipelineState.SubStatus);
            Assert.Contains("after the wait", _console.ToString());
        }

        // A run in its first burst of work is not stored until the burst ends: one this process dispatched or works on is asked to
        // stop all the same, for its own site; any other id is refused - so a stop reaches no other site's run, and none it never
        // knew; and one known too long ago has been let go.
        [Fact]
        public async Task A_run_not_stored_yet_is_stopped_when_this_process_knows_it_for_the_same_site()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var tenants = services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>();
            var handler = new CancelRunCommandHandler(services.GetRequiredService<IPipelineRuntime>(), services.GetRequiredService<Stomme.Engine.Pipelines.Management.IPipelineInstanceStore>(), _stops, _clock, tenants);

            Assert.False(await handler.Handle(new CancelRunCommand("never-heard-of"), System.Threading.CancellationToken.None));
            _stops.Seen("another-sites-run", "another site's tenant", _clock.UtcNow);
            Assert.False(await handler.Handle(new CancelRunCommand("another-sites-run"), System.Threading.CancellationToken.None));
            Assert.False(_stops.Take("another-sites-run"));

            _stops.Seen("starting", tenants.TenantId, _clock.UtcNow);
            Assert.True(await handler.Handle(new CancelRunCommand("starting"), System.Threading.CancellationToken.None));
            Assert.True(_stops.Take("starting")); // asked: its next step stops it

            _stops.Seen("long-ago", tenants.TenantId, _clock.UtcNow);
            _clock.UtcNow = _clock.UtcNow.Add(PipelineRunStops.KeptFor).AddMinutes(1);
            Assert.False(await handler.Handle(new CancelRunCommand("long-ago"), System.Threading.CancellationToken.None));
        }

        // A run just started is "not yet" - the API's 204, where it was 404 - while this process knows it for the same site; any
        // other id is none: another site's run, one never heard of, one known too long ago.
        [Fact]
        public async Task A_run_just_started_is_not_yet_for_its_own_site_only()
        {
            using var scope = _services.CreateScope();
            var tenants = scope.ServiceProvider.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>();
            var handler = new IsRunStartingQueryHandler(_stops, tenants, _clock);
            Task<bool> Starting(string id) => handler.Handle(new IsRunStartingQuery(id), System.Threading.CancellationToken.None);
            _stops.Seen("started", tenants.TenantId, _clock.UtcNow);
            _stops.Seen("another-sites-run", "another site's tenant", _clock.UtcNow);

            Assert.True(await Starting("started"));
            Assert.False(await Starting("another-sites-run"));
            Assert.False(await Starting("never-heard-of"));
            Assert.False(await Starting(" "));

            // Stored - the engine says so after each save - it is the store's to answer for: deleted after, it is none.
            _stops.Seen("stored", tenants.TenantId, _clock.UtcNow);
            await new MarkRunStored(_stops, _clock).HandleAsync(
                new Stomme.Engine.Pipelines.Management.Notifications.PipelineInstanceSaved(new Stomme.Engine.Pipelines.Management.Entities.PipelineInstance { Id = "stored" }),
                System.Threading.CancellationToken.None);
            Assert.False(await Starting("stored"));

            _clock.UtcNow = _clock.UtcNow.Add(PipelineRunStops.KeptFor).AddMinutes(1);
            Assert.False(await Starting("started"));
        }

        /// <summary>Answers the stop tool as the module would: "unknown" is no run of the site's, any other id is stopped.</summary>
        private sealed class Stopping : Stomme.Mediator.IMediator
        {
            public System.Collections.Generic.List<object> Sent { get; } = new();

            public Task<TResponse> Send<TResponse>(Stomme.Mediator.IRequest<TResponse> request, System.Threading.CancellationToken ct = default)
            {
                Sent.Add(request);
                object? answer = request switch
                {
                    CancelRunCommand cancel => cancel.Id != "unknown",
                    GetRunQuery query => new PipelineRunView { Id = query.Id, Status = "Finished", SubStatus = "Cancelled" },
                    _ => throw new NotSupportedException(request.GetType().Name),
                };
                return Task.FromResult((TResponse)answer!);
            }

            public Task Publish<TNotification>(TNotification notification, System.Threading.CancellationToken ct = default) where TNotification : Stomme.Mediator.INotification => Task.CompletedTask;

            public System.Collections.Generic.IAsyncEnumerable<TResponse> CreateStream<TResponse>(Stomme.Mediator.IStreamRequest<TResponse> request, System.Threading.CancellationToken ct = default) => throw new NotSupportedException();
        }

        // Over MCP: stop_pipeline_run sends the module's own Stop and answers with the run as stored now; an id that is no run of
        // the site's, or none at all, is an error, and nothing is sent for the second.
        [Fact]
        public async Task Stop_pipeline_run_over_MCP_stops_the_run_it_names()
        {
            var mediator = new Stopping();
            var invoker = new Stomme.Modules.Pipelines.Mcp.PipelinesMcpContributor(mediator).InvokerFor(Stomme.Modules.Pipelines.Mcp.PipelinesMcpContributor.StopPipelineRun)!;
            var stopped = await invoker.InvokeAsync("""{"runId":"r1"}""");
            Assert.False(stopped.IsError);
            Assert.Contains("\"stopped\":true", stopped.ContentJson);
            Assert.Contains("\"subStatus\":\"Cancelled\"", stopped.ContentJson);
            Assert.True((await invoker.InvokeAsync("""{"runId":"unknown"}""")).IsError);
            Assert.True((await invoker.InvokeAsync("""{}""")).IsError);
            Assert.Equal(new object[] { new CancelRunCommand("r1"), new GetRunQuery("r1"), new CancelRunCommand("unknown") }, mediator.Sent);
        }

        [Fact]
        public void The_time_limit_reads_back_from_the_body_and_its_words_are_plain()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var body = PipelineGraphCodec.ToBody(Graph("Stomme.WriteLine", timeoutMinutes: 90), null, services.GetRequiredService<IStepRegistry>(), services.GetRequiredService<IIdentityGenerator>());
            Assert.Equal(90, PipelineGraphCodec.ToGraph(body).TimeoutMinutes);
            var pipeline = services.GetRequiredService<IStepSerializer>().Deserialize(body);
            Assert.Equal(90, PipelineGraphCodec.TimeoutMinutes(pipeline.Metadata)); // as the engine materialises it for a run

            Assert.Equal(("1 minute", "45 minutes", "2 hours", "1 hour 30 minutes"), (PipelineRunTimeout.Said(1), PipelineRunTimeout.Said(45), PipelineRunTimeout.Said(120), PipelineRunTimeout.Said(90)));
            Assert.Equal(((int?)2, (int?)1, (int?)PipelineRunTimeout.MaxMinutes, (int?)null), (PipelineRunTimeout.FromSeconds(90), PipelineRunTimeout.FromSeconds(1), PipelineRunTimeout.FromSeconds(10_000_000), PipelineRunTimeout.FromSeconds(-1)));
        }
    }
}
