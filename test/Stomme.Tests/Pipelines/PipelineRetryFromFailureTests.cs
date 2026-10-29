#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.KeyValues.Features;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Commands;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;
using Stomme.Models.Pipelines;
using Stomme.Modules.Pipelines.Application;
using Stomme.Modules.Pipelines.Expressions;
using Stomme.Modules.Pipelines.Steps;
using Stomme.Modules.Pipelines.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using PipelineGraph = Stomme.Models.Pipelines.PipelineGraph;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// n8n's retry "from the node with the error": a failed run goes on as a new run from the step that failed, on the
    /// published version - what ran before is not run again and what it saved is kept, the failed step runs anew (with a fix the
    /// author published since); refused, saying why, for a run that did not fail or whose failed step is gone. And what a run
    /// saved is kept through a pause, which it was not (found building this).
    /// </summary>
    public sealed class PipelineRetryFromFailureTests : IAsyncLifetime
    {
        private ServiceProvider _services = default!;
        private readonly StringWriter _console = new();
        private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

        private sealed class FakeClock(DateTimeOffset now) : ISystemClock
        {
            public DateTimeOffset UtcNow { get; set; } = now;
        }

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions()));
            services.AddSingleton<IOptions<PipelineTriggerOptions>>(Options.Create(new PipelineTriggerOptions()));
            var console = _console;
            services.AddEngine(module =>
            {
                module.Configure<PipelinesFeature>(pipelines => pipelines.StandardOutStreamProvider = _ => new CapturedOut(console));
                module.AddStepsFrom<PipelineStepsMarker>();
                module.Configure<KeyValueFeature>(_ => { });
            });
            PipelineVariableStorage.Wrap(services); // as the host composes the engine
            services.AddExpressionDescriptorProvider<TemplateExpressionDescriptorProvider>();
            services.AddSingleton(typeof(Stomme.Engine.Common.Services.MemoryStore<>));
            services.AddScoped<IPipelineDispatcher, InlineDispatcher>();
            services.AddSingleton<ISystemClock>(_clock);
            services.AddScoped<DueWaitsTask>();
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync() => await _services.DisposeAsync();

        private sealed class CapturedOut(TextWriter writer) : IStandardOutStreamProvider
        {
            public TextWriter GetTextWriter() => writer;
        }

        /// <summary>Runs a dispatched definition or run at once, as the engine's worker would later.</summary>
        private sealed class InlineDispatcher(ICommandSender commands) : IPipelineDispatcher
        {
            public async Task<DispatchPipelineResponse> DispatchAsync(DispatchPipelineDefinitionRequest request, DispatchPipelineOptions? options, CancellationToken cancellationToken = default)
            {
                await commands.SendAsync(new DispatchPipelineDefinitionCommand(request.DefinitionVersionId) { Input = request.Input, CorrelationId = request.CorrelationId, InstanceId = request.InstanceId }, cancellationToken);
                return DispatchPipelineResponse.Success();
            }

            public async Task<DispatchPipelineResponse> DispatchAsync(DispatchPipelineInstanceRequest request, DispatchPipelineOptions? options, CancellationToken cancellationToken = default)
            {
                await commands.SendAsync(new DispatchPipelineInstanceCommand(request.InstanceId) { StepHandle = request.StepHandle, Input = request.Input }, cancellationToken);
                return DispatchPipelineResponse.Success();
            }

            public Task<DispatchPipelineResponse> DispatchAsync(DispatchTriggerPipelinesRequest request, DispatchPipelineOptions? options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task<DispatchPipelineResponse> DispatchAsync(DispatchResumePipelinesRequest request, DispatchPipelineOptions? options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }

        // Says hello (a side effect a retry must not repeat), saves when it began, adds a day to a date - "nope" is none until the
        // author fixes it - and says what it kept and what the failed step gave.
        private static PipelineGraph Order(string date) => new()
        {
            Variables = { new PipelineGraphVariable { Id = "v-began", Name = "began", TypeName = "String" }, new PipelineGraphVariable { Id = "v-due", Name = "due", TypeName = "String" } },
            Steps =
            {
                new PipelineGraphStep { Id = "hello", Type = "Stomme.WriteLine", Inputs = { ["Text"] = "HELLO" } },
                new PipelineGraphStep { Id = "began", Type = "Stomme.DateAndTime", Inputs = { ["Operation"] = "Format", ["Date"] = "2026-10-01", ["Format"] = "yyyy-MM-dd" }, OutputVariables = { ["Result"] = "v-began" } },
                new PipelineGraphStep { Id = "due", Type = "Stomme.DateAndTime", Inputs = { ["Operation"] = "Add", ["Date"] = date, ["Duration"] = "1 day", ["Format"] = "yyyy-MM-dd" }, OutputVariables = { ["Result"] = "v-due" } },
                new PipelineGraphStep { Id = "said", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = "DUE {{ due }} - began {{ began }}" } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "hello", TargetId = "began" },
                new PipelineGraphConnection { SourceId = "began", SourcePort = DataStep.DoneOutcome, TargetId = "due" },
                new PipelineGraphConnection { SourceId = "due", SourcePort = DataStep.DoneOutcome, TargetId = "said" },
            },
        };

        private static async Task<string> PublishAsync(IServiceProvider services, PipelineGraph graph, string? definitionId = null)
        {
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var registry = services.GetRequiredService<IStepRegistry>();
            var identities = services.GetRequiredService<IIdentityGenerator>();
            var definition = definitionId is null
                ? await publisher.NewAsync(services.GetRequiredService<IStepSerializer>().Deserialize(PipelineGraphCodec.ToBody(graph, null, registry, identities)))
                : (await publisher.GetDraftAsync(definitionId, VersionOptions.Latest))!;
            if (definitionId is not null)
            {
                definition.StringData = PipelineGraphCodec.ToBody(graph, definition.StringData, registry, identities);
                definition = await publisher.SaveDraftAsync(definition);
            }

            definition.Name = "Order";
            var published = await publisher.PublishAsync(definition);
            Assert.True(published.Succeeded, string.Join("; ", published.ValidationErrors.Select(e => e.Message)));
            return definition.DefinitionId;
        }

        private static RetryFromFailedStepCommandHandler Retry(IServiceProvider services) => new(
            services.GetRequiredService<IPipelineInstanceStore>(), services.GetRequiredService<IPipelineDefinitionService>(), services.GetRequiredService<IPipelineInstanceManager>(),
            services.GetRequiredService<IPipelineStateSerializer>(), services.GetRequiredService<IPipelineDispatcher>(), services.GetRequiredService<IIdentityGenerator>(),
            services.GetRequiredService<ISystemClock>(), Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<Stomme.Modules.Pipelines.Reliability.PipelineTurns>(services));

        private static StartPipelineCommandHandler Start(IServiceProvider services) => new(
            services.GetRequiredService<IPipelineDefinitionStore>(), services.GetRequiredService<IPipelineInstanceStore>(), services.GetRequiredService<IPipelineDispatcher>(), services.GetRequiredService<IIdentityGenerator>(),
            new Stomme.Modules.Pipelines.Reliability.PipelineRunStops(), services.GetRequiredService<Stomme.Engine.Common.Multitenancy.ITenantAccessor>(), services.GetRequiredService<ISystemClock>(),
            Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<Stomme.Modules.Pipelines.Reliability.PipelineTurns>(services));

        private static async Task<PipelineInstance> RunOfAsync(IServiceProvider services, string id) =>
            (await services.GetRequiredService<IPipelineInstanceStore>().FindAsync(new PipelineInstanceFilter { Id = id }))!;

        private int Count(string said) => _console.ToString().Split('\n').Count(line => line.Trim() == said);

        [Fact]
        public async Task A_failed_run_goes_on_from_the_step_that_failed_without_running_again_what_had_finished()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var order = await PublishAsync(services, Order("nope"));
            var first = (await Start(services).Handle(new StartPipelineCommand(order, null, null), CancellationToken.None))!.RunId;
            Assert.Equal(PipelineSubStatus.Faulted, (await RunOfAsync(services, first)).SubStatus);
            Assert.Equal(1, Count("HELLO"));

            // On the same version it fails again at the same step - and says hello no more.
            var again = (await Retry(services).Handle(new RetryFromFailedStepCommand(first), CancellationToken.None))!.RunId;
            var failedAgain = await RunOfAsync(services, again);
            Assert.Equal((PipelineSubStatus.Faulted, first), (failedAgain.SubStatus, failedAgain.PipelineState.Properties[PipelineRetryFromFailure.RetryOfProperty]?.ToString()));
            Assert.Equal(1, Count("HELLO"));

            // The author fixes the date and publishes; the retry runs the fixed step, reading what the first run saved.
            await PublishAsync(services, Order("2026-10-07"), order);
            var fixedRun = (await Retry(services).Handle(new RetryFromFailedStepCommand(again), CancellationToken.None))!.RunId;
            Assert.Equal(PipelineSubStatus.Finished, (await RunOfAsync(services, fixedRun)).SubStatus);
            Assert.Equal((1, 1), (Count("HELLO"), Count("DUE 2026-10-08T00:00:00Z - began 2026-10-01")));
            // Each run keeps its own record of what its steps did: the first run its failed step, the last its passes after it.
            var executions = services.GetRequiredService<IStepExecutionStore>();
            async Task<string[]> PassesOf(string runId) => (await executions.FindManyAsync(new StepExecutionRecordFilter { PipelineInstanceId = runId }))
                .Where(pass => pass.StepId is "hello" or "began" or "due" or "said").Select(pass => pass.StepId + ":" + pass.Status).OrderBy(pass => pass, StringComparer.Ordinal).ToArray(); // one moment on this clock
            Assert.Equal(new[] { "began:Completed", "due:Faulted", "hello:Completed" }, await PassesOf(first));
            Assert.Equal(new[] { "due:Faulted" }, await PassesOf(again));
            Assert.Equal(new[] { "due:Completed", "said:Completed" }, await PassesOf(fixedRun));
        }

        // Saves when it began, waits two hours (stored, the run resumed by the due-waits task), then says what it saved.
        private static PipelineGraph Pausing(string hook) => new()
        {
            Variables = { new PipelineGraphVariable { Id = "v-began", Name = "began", TypeName = "String" } },
            Steps =
            {
                new PipelineGraphStep { Id = "hook", Type = "Stomme.Webhook", Inputs = { ["WebhookName"] = hook } },
                new PipelineGraphStep { Id = "began", Type = "Stomme.DateAndTime", Inputs = { ["Operation"] = "Format", ["Date"] = "2026-10-01", ["Format"] = "yyyy-MM-dd" }, OutputVariables = { ["Result"] = "v-began" } },
                new PipelineGraphStep { Id = "wait", Type = "Stomme.Wait", Inputs = { ["For"] = "2 hours" } },
                new PipelineGraphStep { Id = "said", Type = "Stomme.WriteLine", InputExpressions = { ["Text"] = "AFTER " + hook + " {{ began }}" } },
            },
            Connections =
            {
                new PipelineGraphConnection { SourceId = "hook", TargetId = "began" },
                new PipelineGraphConnection { SourceId = "began", SourcePort = DataStep.DoneOutcome, TargetId = "wait" },
                new PipelineGraphConnection { SourceId = "wait", TargetId = "said" },
            },
        };

        private async Task WaitAndResumeAsync(IServiceProvider services, string hook)
        {
            await services.GetRequiredService<IStimulusSender>().SendAsync("Stomme.Webhook", new WebhookStimulus(hook));
            Assert.DoesNotContain("AFTER " + hook, _console.ToString());
            _clock.UtcNow += TimeSpan.FromHours(3);
            await services.GetRequiredService<DueWaitsTask>().ExecuteAsync(CancellationToken.None);
        }

        // Found building the retry: a variable named no storage, so the engine held it in memory only and a run that paused lost
        // it. The codec names the run's storage for every variable; a body saved before it is given the same when it is built.
        [Fact]
        public async Task What_a_run_saved_before_a_pause_is_read_after_it_in_a_body_saved_before_too()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var registry = services.GetRequiredService<IStepRegistry>();
            var identities = services.GetRequiredService<IIdentityGenerator>();
            var body = System.Text.Json.Nodes.JsonNode.Parse(PipelineGraphCodec.ToBody(Pausing("kept"), null, registry, identities))!;
            Assert.All(body["variables"]!.AsArray(), variable => Assert.Equal(PipelineVariableStorage.DriverTypeName, variable!["storageDriverTypeName"]!.GetValue<string>()));

            await PublishAsync(services, Pausing("kept"));
            await WaitAndResumeAsync(services, "kept");
            Assert.Equal(1, Count("AFTER kept 2026-10-01"));

            // A body saved before 10.22 names no storage for its variables.
            foreach (var variable in body["variables"]!.AsArray())
            {
                variable!.AsObject().Remove("storageDriverTypeName");
            }

            var older = body.ToJsonString().Replace("\"kept\"", "\"older\"", StringComparison.Ordinal).Replace("AFTER kept", "AFTER older", StringComparison.Ordinal);
            var publisher = services.GetRequiredService<IPipelineDefinitionPublisher>();
            var definition = await publisher.NewAsync(services.GetRequiredService<IStepSerializer>().Deserialize(older));
            Assert.True((await publisher.PublishAsync(definition)).Succeeded);
            await WaitAndResumeAsync(services, "older");
            Assert.Equal(1, Count("AFTER older 2026-10-01"));
        }

        private sealed class Recording : Stomme.Mediator.IMediator
        {
            public List<object> Sent { get; } = new();

            public Task<TResponse> Send<TResponse>(Stomme.Mediator.IRequest<TResponse> request, CancellationToken ct = default)
            {
                Sent.Add(request);
                return Task.FromResult((TResponse)(object)new PipelineRunStarted { RunId = "again" });
            }

            public Task Publish<TNotification>(TNotification notification, CancellationToken ct = default) where TNotification : Stomme.Mediator.INotification => Task.CompletedTask;

            public IAsyncEnumerable<TResponse> CreateStream<TResponse>(Stomme.Mediator.IStreamRequest<TResponse> request, CancellationToken ct = default) => throw new NotSupportedException();
        }

        // Over MCP: run_pipeline_again, from the failed step or from the start - the module's own two commands - and nothing sent
        // for arguments that do not say which.
        [Fact]
        public async Task Run_pipeline_again_over_MCP_sends_the_retry_it_names()
        {
            var mediator = new Recording();
            var invoker = new Stomme.Modules.Pipelines.Mcp.PipelinesMcpContributor(mediator).InvokerFor(Stomme.Modules.Pipelines.Mcp.PipelinesMcpContributor.RunPipelineAgain)!;
            Assert.False((await invoker.InvokeAsync("""{"runId":"r1","from":"failed-step"}""")).IsError);
            Assert.False((await invoker.InvokeAsync("""{"runId":"r2","from":"start"}""")).IsError);
            Assert.True((await invoker.InvokeAsync("""{"runId":"r3","from":"middle"}""")).IsError);
            Assert.True((await invoker.InvokeAsync("""{"from":"start"}""")).IsError);
            // From the start on the version the run ran; a failed step goes on only on the published version.
            Assert.False((await invoker.InvokeAsync("""{"runId":"r4","from":"start","version":"its-own"}""")).IsError);
            Assert.True((await invoker.InvokeAsync("""{"runId":"r5","from":"failed-step","version":"its-own"}""")).IsError);
            Assert.True((await invoker.InvokeAsync("""{"runId":"r6","from":"start","version":"another"}""")).IsError);
            Assert.Equal(new object[] { new RetryFromFailedStepCommand("r1"), new RetryRunCommand("r2"), new RetryRunCommand("r4", true) }, mediator.Sent);
        }

        // n8n's "retry with original workflow": a run again on the version it ran, beside the published one - the
        // version that failed fails again where the fixed one finishes - and refused, saying why, once that version is not kept.
        [Fact]
        public async Task A_run_is_run_again_on_the_version_it_ran_or_on_the_published_one()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var instances = services.GetRequiredService<IPipelineInstanceStore>();
            var again = new RetryRunCommandHandler(instances, services.GetRequiredService<IPipelineDefinitionStore>(), services.GetRequiredService<IPipelineDispatcher>(), services.GetRequiredService<IIdentityGenerator>(), Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<Stomme.Modules.Pipelines.Reliability.PipelineTurns>(services));

            var order = await PublishAsync(services, Order("nope")); // version 1 fails
            var first = (await Start(services).Handle(new StartPipelineCommand(order, null, null), CancellationToken.None))!.RunId;
            var ran = (await RunOfAsync(services, first)).DefinitionVersionId;
            await PublishAsync(services, Order("2026-10-07"), order); // version 2 fixes it

            var onPublished = await RunOfAsync(services, (await again.Handle(new RetryRunCommand(first), CancellationToken.None))!.RunId);
            var onItsOwn = await RunOfAsync(services, (await again.Handle(new RetryRunCommand(first, OnItsVersion: true), CancellationToken.None))!.RunId);
            Assert.Equal((PipelineSubStatus.Finished, PipelineSubStatus.Faulted), (onPublished.SubStatus, onItsOwn.SubStatus));
            Assert.Equal(ran, onItsOwn.DefinitionVersionId);
            Assert.NotEqual(ran, onPublished.DefinitionVersionId);

            await services.GetRequiredService<IPipelineDefinitionStore>().DeleteAsync(new PipelineDefinitionFilter { Id = ran });
            var gone = await Assert.ThrowsAsync<InvalidOperationException>(() => again.Handle(new RetryRunCommand(first, OnItsVersion: true), CancellationToken.None));
            Assert.Equal("The version this run ran is not kept any more - run it again on the published version.", gone.Message);
        }

        [Fact]
        public async Task A_retry_from_the_failed_step_is_refused_saying_why_when_it_cannot_be()
        {
            using var scope = _services.CreateScope();
            var services = scope.ServiceProvider;
            var start = Start(services);

            var fine = await PublishAsync(services, Order("2026-10-07"));
            var finished = (await start.Handle(new StartPipelineCommand(fine, null, null), CancellationToken.None))!.RunId;
            var notFailed = await Assert.ThrowsAsync<InvalidOperationException>(() => Retry(services).Handle(new RetryFromFailedStepCommand(finished), CancellationToken.None));
            Assert.Equal("Only a run that failed goes on from the step that failed - run it again instead.", notFailed.Message);

            // The step that failed was taken out of the pipeline since.
            var order = await PublishAsync(services, Order("nope"));
            var failed = (await start.Handle(new StartPipelineCommand(order, null, null), CancellationToken.None))!.RunId;
            var without = Order("2026-10-07");
            without.Steps.RemoveAll(step => step.Id == "due");
            without.Connections.RemoveAll(connection => connection.TargetId == "due" || connection.SourceId == "due");
            await PublishAsync(services, without, order);
            var gone = await Assert.ThrowsAsync<InvalidOperationException>(() => Retry(services).Handle(new RetryFromFailedStepCommand(failed), CancellationToken.None));
            Assert.Equal("The step that failed is not in the published version any more - retry the run from the start.", gone.Message);

            // A run whose input its pipeline's setting emptied is neither gone on with nor run again without it - from here
            // or over MCP; one started with nothing is emptied of nothing and stays retryable.
            var unkept = (await start.Handle(new StartPipelineCommand(order, null, null), CancellationToken.None))!.RunId;
            var instances = services.GetRequiredService<IPipelineInstanceStore>();
            var run = await RunOfAsync(services, unkept);
            run.PipelineState.Properties[KeepRunInput.PropertyKey] = new Dictionary<string, object> { ["card"] = "4111" }; // as the host keeps it
            Assert.True(KeepRunInput.Empty(run.PipelineState));
            await instances.SaveAsync(run);
            Assert.True(KeepRunInput.WasEmptied((await RunOfAsync(services, unkept)).PipelineState));
            var goOn = await Assert.ThrowsAsync<InvalidOperationException>(() => Retry(services).Handle(new RetryFromFailedStepCommand(unkept), CancellationToken.None));
            Assert.Equal(KeepRunInput.NotKeptRefusal, goOn.Message);
            var again = new RetryRunCommandHandler(instances, services.GetRequiredService<IPipelineDefinitionStore>(), services.GetRequiredService<IPipelineDispatcher>(), services.GetRequiredService<IIdentityGenerator>(), Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<Stomme.Modules.Pipelines.Reliability.PipelineTurns>(services));
            var runAgain = await Assert.ThrowsAsync<InvalidOperationException>(() => again.Handle(new RetryRunCommand(unkept), CancellationToken.None));
            Assert.Equal(KeepRunInput.NotKeptRefusal, runAgain.Message);

            var nothing = await RunOfAsync(services, finished);
            Assert.False(KeepRunInput.Empty(nothing.PipelineState));
            Assert.False(KeepRunInput.WasEmptied(nothing.PipelineState));
        }
    }
}
