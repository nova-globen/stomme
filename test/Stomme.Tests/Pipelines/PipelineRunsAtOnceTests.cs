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
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.ActivationValidators;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Steps;
using Stomme.Models.Pipelines;
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
    /// BL-229 - how many runs of a pipeline may go at once: set on the definition's engine options at each save, a trigger's start
    /// while a run is going (or waiting) is skipped by the engine - in all, or for the same correlation id - and the module's own
    /// starts, Run and Retry, are refused saying why.
    /// </summary>
    public sealed class PipelineRunsAtOnceTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineTriggerOptions>>(Options.Create(new PipelineTriggerOptions()));
            services.AddEngine(module =>
            {
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<KeyValueFeature>(_ => { });
            });
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            // One at a time, the rest in turn - composed as the module's startup composes it.
            services.AddScoped<IPipelineActivationStrategy, Stomme.Modules.Pipelines.Reliability.InTurnStrategy>();
            services.AddScoped<DefaultPipelineStarter>();
            services.AddScoped<IPipelineStarter, Stomme.Modules.Pipelines.Reliability.InTurnPipelineStarter>();
            services.AddScoped<Stomme.Modules.Pipelines.Reliability.PipelineTurns>();
            services.AddScoped<Stomme.Modules.Pipelines.Reliability.PipelineTurnsTask>();
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        // A webhook starts it and a Wait of an hour keeps it going, as a long run or one waiting for a decision would.
        private static async Task<string> PublishAsync(IServiceProvider services, string hook, string? runsAtOnce)
        {
            var trigger = new Webhook { WebhookName = new(hook), Id = "trigger" };
            trigger.SetCanStartPipeline(true);
            var wait = new Wait { Id = "wait", For = new("1 hour") };
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(new Flowchart
            {
                Id = "flow",
                Steps = { trigger, wait },
                Connections = { new Connection(new Endpoint(trigger), new Endpoint(wait)) },
            });
            definition.Name = "Busy " + hook;
            PipelineRunPolicy.Apply(definition, runsAtOnce);
            Assert.True((await publisher.PublishAsync(definition)).Succeeded);
            return definition.DefinitionId;
        }

        private static async Task<string[]> RunsOf(IServiceProvider services, string definitionId) =>
            (await services.GetRequiredService<IPipelineInstanceStore>().FindManyAsync(new PipelineInstanceFilter { DefinitionId = definitionId }))
                .Select(run => run.CorrelationId ?? "").OrderBy(id => id, StringComparer.Ordinal).ToArray();

        private static Task SendAsync(IServiceProvider services, string hook, string? correlationId = null) =>
            services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus(hook), new StimulusMetadata
            {
                CorrelationId = correlationId,
                Input = new System.Collections.Generic.Dictionary<string, object> { [Webhook.PayloadInputKey] = "body " + correlationId },
            });

        // BL-229 - one at a time, the rest in turn: a start while a run is going is a run that waits - Queued, with what it was started
        // with - and the task starts the oldest once none is going, through its trigger, as the trigger would have; none is lost,
        // none overtakes another; a waiting run stopped is out of the line; Run waits its turn too.
        [Fact]
        public async Task In_turn_a_start_while_a_run_is_going_waits_and_the_oldest_starts_through_its_trigger_when_none_is_going()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var instances = services.GetRequiredService<IPipelineInstanceStore>();
            var task = services.GetRequiredService<Stomme.Modules.Pipelines.Reliability.PipelineTurnsTask>();
            var runtime = services.GetRequiredService<IPipelineRuntime>();
            var turn = await PublishAsync(services, "turn", PipelineRunsAtOnce.OneInTurn);

            async Task<PipelineInstance[]> RunsAsync() =>
                (await instances.FindManyAsync(new PipelineInstanceFilter { DefinitionId = turn })).OrderBy(run => run.CreatedAt).ThenBy(run => run.CorrelationId, StringComparer.Ordinal).ToArray();
            string Shape(PipelineInstance run) => run.CorrelationId + ":" + run.SubStatus + (Stomme.Modules.Pipelines.Reliability.PipelineTurns.IsWaiting(run.PipelineState) ? " waiting" : "");
            async Task<string[]> ShapesAsync() => (await RunsAsync()).Select(Shape).ToArray();

            // Nothing going: the first runs at once (to its Wait); the next two wait their turn, each with its own input kept.
            await SendAsync(services, "turn", "a");
            await SendAsync(services, "turn", "b");
            await SendAsync(services, "turn", "c");
            Assert.Equal(new[] { "a:Suspended", "b:Pending waiting", "c:Pending waiting" }, await ShapesAsync());
            var queued = (await RunsAsync())[1];
            Assert.Equal("body b", queued.PipelineState.Input[Webhook.PayloadInputKey]?.ToString());
            var listed = await new ListPipelinesQueryHandler(services.GetRequiredService<IPipelineDefinitionStore>(), instances,
                    services.GetRequiredService<Stomme.Engine.KeyValues.Contracts.IKeyValueStore>(), services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>())
                .Handle(new ListPipelinesQuery(WithLastRuns: true), CancellationToken.None);
            Assert.Equal(2, listed.Single(pipeline => pipeline.DefinitionId == turn).Queued); // the list says how many wait

            // While a runs, the task starts nothing.
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(new[] { "a:Suspended", "b:Pending waiting", "c:Pending waiting" }, await ShapesAsync());

            // a ends: b - the oldest - has its turn, and starts through its trigger: it waits at its Wait, not at the webhook.
            await (await runtime.CreateClientAsync((await RunsAsync())[0].Id)).CancelAsync();
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal(new[] { "a:Cancelled", "b:Suspended", "c:Pending waiting" }, await ShapesAsync());
            var b = (await RunsAsync())[1];
            Assert.Equal(new[] { Wait.StepTypeName }, b.PipelineState.Bookmarks.Select(bookmark => bookmark.Name).ToArray());
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal("c:Pending waiting", (await ShapesAsync())[2]); // b is going

            // A waiting run stopped is out of the line; Run waits its turn behind what is there.
            var start = new StartPipelineCommandHandler(services.GetRequiredService<IPipelineDefinitionStore>(), instances, services.GetRequiredService<IPipelineDispatcher>(),
                services.GetRequiredService<IIdentityGenerator>(), new Stomme.Modules.Pipelines.Reliability.PipelineRunStops(),
                services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>(), services.GetRequiredService<Stomme.Engine.Common.ISystemClock>(),
                services.GetRequiredService<Stomme.Modules.Pipelines.Reliability.PipelineTurns>());
            await (await runtime.CreateClientAsync((await RunsAsync())[2].Id)).CancelAsync();
            Assert.Equal("body c", KeepRunInput.Of((await RunsAsync())[2].PipelineState)[Webhook.PayloadInputKey]?.ToString()); // kept to run again
            var byRun = await start.Handle(new StartPipelineCommand(turn, "d", null), CancellationToken.None);
            Assert.True(byRun!.Queued);
            Assert.Equal(new[] { "a:Cancelled", "b:Suspended", "c:Cancelled", "d:Pending waiting" }, await ShapesAsync());
            await (await runtime.CreateClientAsync(b.Id)).CancelAsync();
            await task.ExecuteAsync(CancellationToken.None);
            Assert.Equal("d:Suspended", (await ShapesAsync())[3]); // a run started by Run starts from the beginning, to its Wait
            Assert.Equal(byRun.RunId, (await RunsAsync())[3].Id);
        }

        [Fact]
        public async Task A_triggers_start_while_a_run_is_going_is_skipped_in_all_or_per_correlation_id()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;

            var any = await PublishAsync(services, "any", null);
            await SendAsync(services, "any");
            await SendAsync(services, "any");
            Assert.Equal(2, (await RunsOf(services, any)).Length);

            var one = await PublishAsync(services, "one", PipelineRunsAtOnce.One);
            await SendAsync(services, "one", "a");
            await SendAsync(services, "one", "b");
            Assert.Equal(new[] { "a" }, await RunsOf(services, one)); // the second waited for nothing: it was not started

            var perCorrelation = await PublishAsync(services, "each", PipelineRunsAtOnce.OnePerCorrelation);
            await SendAsync(services, "each", "a");
            await SendAsync(services, "each", "a");
            await SendAsync(services, "each", "b");
            Assert.Equal(new[] { "a", "b" }, await RunsOf(services, perCorrelation));
        }

        [Fact]
        public async Task Run_and_Retry_are_refused_saying_why_while_a_run_is_going_and_allowed_otherwise()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var definitions = services.GetRequiredService<IPipelineDefinitionStore>();
            var instances = services.GetRequiredService<IPipelineInstanceStore>();
            var start = new StartPipelineCommandHandler(definitions, instances, services.GetRequiredService<IPipelineDispatcher>(), services.GetRequiredService<IIdentityGenerator>(),
                new Stomme.Modules.Pipelines.Reliability.PipelineRunStops(), services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>(), services.GetRequiredService<Stomme.Engine.Common.ISystemClock>(), Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<Stomme.Modules.Pipelines.Reliability.PipelineTurns>(services));
            var retry = new RetryRunCommandHandler(instances, definitions, services.GetRequiredService<IPipelineDispatcher>(), services.GetRequiredService<IIdentityGenerator>(), Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<Stomme.Modules.Pipelines.Reliability.PipelineTurns>(services));

            var one = await PublishAsync(services, "single", PipelineRunsAtOnce.One);
            Assert.NotNull(await start.Handle(new StartPipelineCommand(one, null, null), CancellationToken.None)); // nothing going yet
            await SendAsync(services, "single", "a");
            var going = Assert.Single(await instances.FindManyAsync(new PipelineInstanceFilter { DefinitionId = one }));
            var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => start.Handle(new StartPipelineCommand(one, null, null), CancellationToken.None));
            Assert.Contains("runs one at a time", refused.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => retry.Handle(new RetryRunCommand(going.Id), CancellationToken.None));

            var each = await PublishAsync(services, "per-order", PipelineRunsAtOnce.OnePerCorrelation);
            await SendAsync(services, "per-order", "order-1");
            var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => start.Handle(new StartPipelineCommand(each, "order-1", null), CancellationToken.None));
            Assert.Contains("'order-1'", busy.Message);
            Assert.NotNull(await start.Handle(new StartPipelineCommand(each, "order-2", null), CancellationToken.None));
        }

        // BL-229: the waiting runs take their turns in the order their calls ARRIVED - stamped as each is handed to the engine, which
        // takes them on several workers and so stores them in no fixed order (on UAT three calls sent one after another were
        // stored in reverse). Here c is stored before b but arrived after it: b goes first.
        [Fact]
        public async Task In_turn_the_waiting_runs_go_in_the_order_their_calls_arrived_not_the_order_they_were_stored()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var instances = services.GetRequiredService<IPipelineInstanceStore>();
            var task = services.GetRequiredService<Stomme.Modules.Pipelines.Reliability.PipelineTurnsTask>();
            var runtime = services.GetRequiredService<IPipelineRuntime>();
            var turn = await PublishAsync(services, "arrival", PipelineRunsAtOnce.OneInTurn);
            var t0 = DateTimeOffset.UtcNow.AddMinutes(-1);
            Task SendAt(string correlation, DateTimeOffset arrived) =>
                services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus("arrival"), new StimulusMetadata
                {
                    CorrelationId = correlation,
                    Properties = new System.Collections.Generic.Dictionary<string, object> { [Stomme.Modules.Pipelines.Reliability.PipelineTurns.ArrivedKey] = arrived.UtcTicks },
                });
            async Task<PipelineInstance[]> RunsAsync() => (await instances.FindManyAsync(new PipelineInstanceFilter { DefinitionId = turn })).OrderBy(run => run.CorrelationId, StringComparer.Ordinal).ToArray();

            await SendAt("a", t0);
            await SendAt("c", t0.AddSeconds(3));
            await SendAt("b", t0.AddSeconds(2));
            var runs = await RunsAsync();
            Assert.Equal((t0.AddSeconds(2), t0.AddSeconds(3)), (runs[1].CreatedAt, runs[2].CreatedAt)); // each stored with when it arrived
            await (await runtime.CreateClientAsync(runs[0].Id)).CancelAsync();
            await task.ExecuteAsync(CancellationToken.None);
            runs = await RunsAsync();
            Assert.Equal(new[] { "a:Cancelled", "b:Suspended", "c:Pending" }, runs.Select(run => run.CorrelationId + ":" + run.SubStatus).ToArray());
        }

        // The stamp: put on as a call is handed to the engine, and a stamp the sender set is kept.
        [Fact]
        public async Task A_call_is_stamped_with_its_arrival_as_it_is_handed_to_the_engine()
        {
            var inner = new CapturingDispatcher();
            var at = new DateTimeOffset(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);
            var dispatcher = new Stomme.Modules.Pipelines.Reliability.ArrivalStampingStimulusDispatcher(inner, new FixedClock(at));
            await dispatcher.SendAsync(new Stomme.Engine.Pipelines.Runtime.Requests.DispatchStimulusRequest { StepTypeName = "Stomme.Webhook" });
            Assert.Equal(at, Stomme.Modules.Pipelines.Reliability.PipelineTurns.ArrivedAt(inner.Sent!.Metadata!.Properties));
            var earlier = at.AddMinutes(-5);
            await dispatcher.SendAsync(new Stomme.Engine.Pipelines.Runtime.Requests.DispatchStimulusRequest
            {
                Metadata = new StimulusMetadata { Properties = new System.Collections.Generic.Dictionary<string, object> { [Stomme.Modules.Pipelines.Reliability.PipelineTurns.ArrivedKey] = earlier.UtcTicks } },
            });
            Assert.Equal(earlier, Stomme.Modules.Pipelines.Reliability.PipelineTurns.ArrivedAt(inner.Sent!.Metadata!.Properties));
        }

        private sealed class CapturingDispatcher : IStimulusDispatcher
        {
            public Stomme.Engine.Pipelines.Runtime.Requests.DispatchStimulusRequest? Sent { get; private set; }

            public Task<Stomme.Engine.Pipelines.Runtime.Responses.DispatchStimulusResponse> SendAsync(Stomme.Engine.Pipelines.Runtime.Requests.DispatchStimulusRequest request, CancellationToken cancellationToken = default)
            {
                Sent = request;
                return Task.FromResult(Stomme.Engine.Pipelines.Runtime.Responses.DispatchStimulusResponse.Empty);
            }
        }

        private sealed class FixedClock(DateTimeOffset now) : Stomme.Engine.Common.ISystemClock
        {
            public DateTimeOffset UtcNow => now;
        }

        [Fact]
        public void The_setting_becomes_the_engines_activation_strategy_and_an_unknown_one_is_any_number()
        {
            var definition = new Stomme.Engine.Pipelines.Management.Entities.PipelineDefinition();
            PipelineRunPolicy.Apply(definition, "ONE");
            Assert.Equal(typeof(SingletonStrategy), definition.Options.ActivationStrategyType);
            PipelineRunPolicy.Apply(definition, PipelineRunsAtOnce.OnePerCorrelation);
            Assert.Equal(typeof(CorrelatedSingletonStrategy), definition.Options.ActivationStrategyType);
            PipelineRunPolicy.Apply(definition, PipelineRunsAtOnce.OneInTurn);
            Assert.Equal(typeof(Stomme.Modules.Pipelines.Reliability.InTurnStrategy), definition.Options.ActivationStrategyType);
            Assert.True(Stomme.Modules.Pipelines.Reliability.PipelineTurns.IsInTurn(definition));
            PipelineRunPolicy.Apply(definition, "twice");
            Assert.Null(definition.Options.ActivationStrategyType);
            PipelineRunPolicy.Apply(definition, PipelineRunsAtOnce.One);
            PipelineRunPolicy.Apply(definition, null);
            Assert.Null(definition.Options.ActivationStrategyType);

            // A file carries it, as it carries the rest of the pipeline.
            var file = PipelineExchange.Write("Nightly", null, new PipelineGraph { RunsAtOnce = PipelineRunsAtOnce.One });
            Assert.Equal(PipelineRunsAtOnce.One, PipelineExchange.Read(file).Graph!.RunsAtOnce);
        }
    }
}
