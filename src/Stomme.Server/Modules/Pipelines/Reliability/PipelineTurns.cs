#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Common.DistributedHosting;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Common.RecurringTasks;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Contracts;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Options;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Messages;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.State;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Models.Pipelines;
using Medallion.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Reliability
{
    /// <summary>
    /// One at a time, the REST IN TURN (n8n's concurrency control queues what it cannot run yet; "one at a time" here
    /// skipped a trigger's start and refused Run's, so a webhook called during a run was answered 202 and never ran). A start of
    /// such a pipeline is STORED as a run before anything runs - under the pipeline's turn lock, so two starts cannot both find
    /// nothing going - and waits its turn when a run of it is going or waiting: a run like any other, listed as Queued, with what
    /// it was started with, cancelled like any other. <see cref="PipelineTurnsTask"/> starts the oldest waiting run once none
    /// of the pipeline's runs is going. A waiting run is marked in its state's properties, which the engine keeps with the run.
    /// </summary>
    public sealed class PipelineTurns(
        IPipelineInstanceStore instances,
        IPipelineInstanceManager runs,
        IPipelineInstanceFactory factory,
        IPipelineRuntime runtime,
        IPipelineDefinitionService definitions,
        IPipelineDispatcher dispatcher,
        IDistributedLockProvider locks,
        IOptions<DistributedLockingOptions> lockOptions,
        ITenantAccessor tenants,
        ISystemClock clock)
    {
        /// <summary>A run's state property while it waits its turn.</summary>
        public const string WaitingKey = "stomme:waitingTurn";

        /// <summary>The trigger step a waiting run came through - it starts there, as the trigger would have started it.</summary>
        public const string TriggerKey = "stomme:turnTrigger";

        /// <summary>
        /// When a trigger's call arrived (UTC ticks), stamped by <see cref="ArrivalStampingStimulusDispatcher"/> as it is handed to the
        /// engine: the engine takes calls on several workers, so they are stored in no fixed order - the arrival is what orders the
        /// waiting runs.
        /// </summary>
        public const string ArrivedKey = "stomme:arrivedAt";

        /// <summary>A call's arrival as its properties carry it; null when they do not.</summary>
        public static DateTimeOffset? ArrivedAt(IDictionary<string, object>? properties) =>
            properties is not null && properties.TryGetValue(ArrivedKey, out var value) && long.TryParse(value?.ToString(), out var ticks) && ticks > 0
                ? new DateTimeOffset(ticks, TimeSpan.Zero)
                : null;

        public static bool IsInTurn(Pipeline pipeline) => pipeline.Options?.ActivationStrategyType == typeof(InTurnStrategy);

        public static bool IsInTurn(Engine.Pipelines.Management.Entities.PipelineDefinition definition) => definition.Options?.ActivationStrategyType == typeof(InTurnStrategy);

        /// <summary>The run waits its turn: still going, and marked - a waiting run cancelled keeps its mark and waits for nothing.</summary>
        public static bool IsWaiting(PipelineState state) =>
            state.Status == PipelineStatus.Running && state.Properties.TryGetValue(WaitingKey, out var value) && value?.ToString() == "true";

        /// <summary>
        /// Stores a run of <paramref name="pipeline"/> - waiting its turn when a run of it is going or waiting, else to be run now
        /// by the caller - and says which. Its input is kept whole with it (a run the engine stores later keeps only declared inputs).
        /// </summary>
        public async Task<(PipelineState Run, bool Waiting)> StoreAsync(Pipeline pipeline, PipelineInstanceOptions options, string? triggerStepId, CancellationToken ct,
            DateTimeOffset? arrived = null)
        {
            var state = factory.CreatePipelineState(pipeline, options);
            return (state, await StoreAsync(state, triggerStepId, ct, arrived));
        }

        /// <summary>Stores <paramref name="state"/> (a run not stored yet), waiting its turn when a run of its pipeline is going or waiting; true when it waits.</summary>
        public async Task<bool> StoreAsync(PipelineState state, string? triggerStepId, CancellationToken ct, DateTimeOffset? arrived = null)
        {
            // What it was started with, kept as a started run keeps it (KeepRunInput): the engine keeps only declared inputs once
            // it writes the run's state, so a waiting run cancelled - and run again - had lost its body (found driving it).
            if (state.Input is { Count: > 0 } input && !state.Properties.ContainsKey(Application.KeepRunInput.PropertyKey))
            {
                state.Properties[Application.KeepRunInput.PropertyKey] = new Dictionary<string, object>(input);
            }

            await using var _ = await LockAsync(state.DefinitionId, ct);
            // Its time is when its call ARRIVED, when the call says (a trigger's, stamped as it was handed to the engine), else when it
            // is stored, under the lock - the order runs take their turns in. A time taken before the lock let the run stored second
            // be the older (measured under the gate's load); and the engine takes a trigger's calls on several workers, so three calls
            // sent one after another were stored in the reverse order (measured).
            var now = clock.UtcNow;
            state.CreatedAt = state.UpdatedAt = arrived is { } at && at < now ? at : now;
            var busy = await instances.CountAsync(new PipelineInstanceFilter { DefinitionId = state.DefinitionId, PipelineStatus = PipelineStatus.Running }, ct) > 0;
            if (busy)
            {
                state.Properties[WaitingKey] = "true";
                if (!string.IsNullOrEmpty(triggerStepId))
                {
                    state.Properties[TriggerKey] = triggerStepId;
                }
            }

            await runs.CreateAsync(state, ct);
            return busy;
        }

        /// <summary>
        /// A start the module makes itself - Run, Run again, MCP's run_pipeline - of the version <paramref name="definitionVersionId"/>:
        /// stored, then dispatched when nothing of the pipeline is going (never awaited, as every start here is), or left waiting.
        /// </summary>
        public async Task<PipelineRunStarted> StartAsync(string definitionVersionId, PipelineInstanceOptions options, CancellationToken ct)
        {
            var graph = await definitions.FindPipelineGraphAsync(PipelineDefinitionHandle.ByDefinitionVersionId(definitionVersionId), ct)
                ?? throw new InvalidOperationException("The pipeline's version to run is not there any more.");
            return await StartAsync(factory.CreatePipelineState(graph.Pipeline, options), ct);
        }

        /// <summary>The same for a run already prepared (a run going on from its failed step).</summary>
        public async Task<PipelineRunStarted> StartAsync(PipelineState state, CancellationToken ct)
        {
            if (await StoreAsync(state, null, ct))
            {
                return new PipelineRunStarted { RunId = state.Id, Queued = true };
            }

            var response = await dispatcher.DispatchAsync(new DispatchPipelineInstanceRequest(state.Id), options: null, ct);
            if (response.Fault is not null)
            {
                // Stored and never to run, it would hold the pipeline's turn for ever.
                await instances.DeleteAsync(new PipelineInstanceFilter { Id = state.Id }, ct);
                throw new InvalidOperationException("The run could not be dispatched: " + response.Fault.Message);
            }

            return new PipelineRunStarted { RunId = state.Id };
        }

        /// <summary>
        /// The pipeline's oldest waiting run, now taken - no longer waiting, so it is the run going - when none of the pipeline's
        /// runs is going; null when one is, or none waits. The caller runs it (<see cref="RunAsync"/>).
        /// </summary>
        public async Task<(string RunId, string? TriggerStepId)?> TakeNextAsync(string definitionId, CancellationToken ct)
        {
            await using var _ = await LockAsync(definitionId, ct);
            var going = (await instances.FindManyAsync(new PipelineInstanceFilter { DefinitionId = definitionId, PipelineStatus = PipelineStatus.Running }, ct)).ToList();
            if (going.Count == 0 || going.Any(run => !IsWaiting(run.PipelineState)))
            {
                return null;
            }

            var next = going.OrderBy(run => run.CreatedAt).ThenBy(run => run.Id, StringComparer.Ordinal).First();
            var trigger = next.PipelineState.Properties.TryGetValue(TriggerKey, out var step) ? step?.ToString() : null;
            next.PipelineState.Properties.Remove(WaitingKey);
            next.PipelineState.Properties.Remove(TriggerKey);
            await instances.SaveAsync(next, ct);
            return (next.Id, string.IsNullOrEmpty(trigger) ? null : trigger);
        }

        /// <summary>Runs a stored run from its start - through its trigger step when it came through one - in the caller's context.</summary>
        public async Task<RunPipelineInstanceResponse> RunAsync(string runId, string? triggerStepId, CancellationToken ct)
        {
            var client = await runtime.CreateClientAsync(runId, ct);
            return await client.RunInstanceAsync(new RunPipelineInstanceRequest { TriggerStepId = triggerStepId }, ct);
        }

        /// <summary>The definitions on this tenant with a run waiting its turn.</summary>
        public async Task<IReadOnlyList<string>> WithWaitingAsync(CancellationToken ct) =>
            (await instances.FindManyAsync(new PipelineInstanceFilter { PipelineStatus = PipelineStatus.Running }, ct))
                .Where(run => IsWaiting(run.PipelineState) && !string.IsNullOrEmpty(run.DefinitionId))
                .Select(run => run.DefinitionId).Distinct(StringComparer.Ordinal).ToList();

        // One lock per pipeline per site, through the engine's own lock provider (a file lock on this install: every process on
        // the machine; nodes sharing nothing share no lock - the engine's own singleton has the same reach).
        private async Task<IDistributedSynchronizationHandle> LockAsync(string definitionId, CancellationToken ct) =>
            await locks.AcquireLockAsync($"pipelines-turn:{tenants.TenantId}:{definitionId}", lockOptions.Value.LockAcquisitionTimeout, ct);
    }

    /// <summary>
    /// The engine's activation strategy for "one at a time, the rest in turn" - what the pipeline's options name, so the module
    /// knows it. A trigger's start never reaches it (<see cref="InTurnPipelineStarter"/> stores it first); a start that did would
    /// be judged as "one at a time" judges it - no other run going - rather than let through.
    /// </summary>
    public sealed class InTurnStrategy(IPipelineInstanceStore instances) : IPipelineActivationStrategy
    {
        public async ValueTask<bool> GetAllowActivationAsync(PipelineInstantiationStrategyContext context) =>
            await instances.CountAsync(new PipelineInstanceFilter { DefinitionId = context.Pipeline.Identity.DefinitionId, PipelineStatus = PipelineStatus.Running }, context.CancellationToken) == 0;
    }

    /// <summary>
    /// Every trigger's start (a webhook, a schedule, an event, a failure, a form, a chat) goes through the engine's starter. For a
    /// pipeline that runs in turn, this one stores the run first: waiting, it is answered as started - Queued - and waits its
    /// turn; with nothing going, it is run here, as the engine's starter would have run it. Any other pipeline goes on to the
    /// engine's starter unchanged.
    /// </summary>
    public sealed class InTurnPipelineStarter(DefaultPipelineStarter engine, IPipelineDefinitionService definitions, PipelineTurns turns, ILogger<InTurnPipelineStarter> logger)
        : IPipelineStarter
    {
        public async Task<StartPipelineResponse> StartPipelineAsync(Engine.Pipelines.Runtime.StartPipelineRequest request, CancellationToken cancellationToken = default)
        {
            var pipeline = request.Pipeline
                ?? (request.PipelineDefinitionHandle is { } handle ? (await definitions.FindPipelineGraphAsync(handle, cancellationToken))?.Pipeline : null);
            if (pipeline is null || !PipelineTurns.IsInTurn(pipeline))
            {
                return await engine.StartPipelineAsync(request, cancellationToken);
            }

            var arrived = PipelineTurns.ArrivedAt(request.Properties);
            var (run, waiting) = await turns.StoreAsync(pipeline, new PipelineInstanceOptions
            {
                CorrelationId = request.CorrelationId,
                Name = request.Name,
                Input = request.Input,
                Properties = request.Properties is null ? null : new Dictionary<string, object>(request.Properties),
                ParentPipelineInstanceId = request.ParentId,
            }, request.TriggerStepId, cancellationToken, arrived);
            if (waiting)
            {
                logger.LogInformation("Pipeline {Definition} runs one at a time: run {Run} waits its turn.", pipeline.Identity.DefinitionId, run.Id);
                return new StartPipelineResponse { PipelineInstanceId = run.Id, Status = PipelineStatus.Running, SubStatus = PipelineSubStatus.Pending };
            }

            var response = await turns.RunAsync(run.Id, request.TriggerStepId, cancellationToken);
            return new StartPipelineResponse
            {
                PipelineInstanceId = response.PipelineInstanceId,
                Status = response.Status,
                SubStatus = response.SubStatus,
                Bookmarks = response.Bookmarks,
                Incidents = response.Incidents,
            };
        }
    }

    /// <summary>
    /// The engine's stimulus dispatcher with the call's ARRIVAL stamped on it (<see cref="PipelineTurns.ArrivedKey"/>) as it is handed
    /// over - a webhook's, a form's, an event's: the engine runs them on several workers, so the order they are stored in is no
    /// order at all, and a pipeline that runs in turn orders its waiting runs by this instead. A stamp the sender set is kept.
    /// </summary>
    public sealed class ArrivalStampingStimulusDispatcher(IStimulusDispatcher engine, ISystemClock clock) : IStimulusDispatcher
    {
        public Task<Engine.Pipelines.Runtime.Responses.DispatchStimulusResponse> SendAsync(Engine.Pipelines.Runtime.Requests.DispatchStimulusRequest request, CancellationToken cancellationToken = default)
        {
            request.Metadata ??= new StimulusMetadata();
            request.Metadata.Properties ??= new Dictionary<string, object>();
            request.Metadata.Properties.TryAdd(PipelineTurns.ArrivedKey, clock.UtcNow.UtcTicks);
            return engine.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Starts the next waiting run of each pipeline that runs in turn, once none of its runs is going: a recurring task the ENGINE
    /// runs per site tenant, on one node at a time (<see cref="SingleNodeTaskAttribute"/>), as the schedules and the waits are.
    /// The run is run here, in the task's tick, as the trigger would have run it - so the next tick finds it going until it ends
    /// or waits. One that fails to start is logged and has ended or still counts as going; nothing waiting is lost.
    /// </summary>
    [SingleNodeTask]
    public sealed class PipelineTurnsTask(PipelineTurns turns, ITenantAccessor tenants, ILogger<PipelineTurnsTask> logger) : RecurringTask
    {
        public override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            foreach (var definitionId in await turns.WithWaitingAsync(cancellationToken))
            {
                try
                {
                    if (await turns.TakeNextAsync(definitionId, cancellationToken) is { } next)
                    {
                        logger.LogInformation("Pipeline {Definition} on tenant {Tenant}: run {Run} has its turn.", definitionId, tenants.TenantId, next.RunId);
                        await turns.RunAsync(next.RunId, next.TriggerStepId, cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Pipeline {Definition} on tenant {Tenant}: its next run could not be started: {Error}", definitionId, tenants.TenantId, ex.Message);
                }
            }
        }
    }
}
