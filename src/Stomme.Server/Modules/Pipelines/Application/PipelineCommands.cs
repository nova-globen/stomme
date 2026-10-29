#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Security.Abstractions;

namespace Stomme.Modules.Pipelines.Application
{
    // Every command runs inside the request's engine tenant (EngineTenantMiddleware): what it creates belongs to the
    // request's site, and what it looks up is the site's. The permission is decided by the host's authorization
    // behaviour before any handler runs; a handler never checks a role.

    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record CreatePipelineCommand(string Name, string? Description) : ICommand<PipelineView>;

    public sealed class CreatePipelineCommandHandler(IPipelineDefinitionPublisher publisher) : ICommandHandler<CreatePipelineCommand, PipelineView>
    {
        public async Task<PipelineView> Handle(CreatePipelineCommand request, CancellationToken ct)
        {
            var name = Names.Require(request.Name);
            // A new pipeline is an empty sequence until the editor gives it a body; it runs to Finished as is.
            var definition = await publisher.NewAsync(new Sequence(), ct);
            definition.Name = name;
            definition.Description = request.Description?.Trim();
            definition = await publisher.SaveDraftAsync(definition, ct);
            return PipelineWire.ToView(definition);
        }
    }

    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record UpdatePipelineCommand(string DefinitionId, string Name, string? Description, string? Body) : ICommand<PipelineView?>;

    // The body is the ROOT STEP's document (what NewAsync writes), so the step serializer judges it - not the pipeline
    // serializer, which reads a whole pipeline and would refuse every real body (measured with the wire-format probe). BL-229:
    // the editor renames a pipeline through it, so it says who saved the version and tells the pipeline's other editors, as
    // the graph's save does.
    public sealed class UpdatePipelineCommandHandler(IPipelineDefinitionPublisher publisher, IStepSerializer serializer,
        Stomme.Modules.Collaboration.Realtime.ICollaborationNotifier notifier, ICurrentUser currentUser)
        : ICommandHandler<UpdatePipelineCommand, PipelineView?>
    {
        public async Task<PipelineView?> Handle(UpdatePipelineCommand request, CancellationToken ct)
        {
            var name = Names.Require(request.Name);
            // The latest draft, or a new draft version over the latest published one - the publisher's rule, so a
            // published version is never edited in place.
            var draft = await publisher.GetDraftAsync(request.DefinitionId, VersionOptions.Latest, ct);
            if (draft is null)
            {
                return null;
            }

            if (request.Body is not null)
            {
                try
                {
                    serializer.Deserialize(request.Body);
                }
                catch (Exception ex)
                {
                    throw new ArgumentException("The pipeline body is not a definition the engine can read: " + ex.Message, nameof(request.Body), ex);
                }

                draft.StringData = request.Body;
            }

            draft.Name = name;
            draft.Description = request.Description?.Trim();
            PipelineWire.StampSavedBy(draft, currentUser.UserName, currentUser.UserId);
            draft = await publisher.SaveDraftAsync(draft, ct);
            await notifier.NotifyAsync(Stomme.Collaboration.CollaborativeResourceTypes.Pipeline, request.DefinitionId, Stomme.Collaboration.CollaborationNoticeKinds.Saved, currentUser.UserId ?? 0, ct);
            return PipelineWire.ToView(draft);
        }
    }

    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record PublishPipelineCommand(string DefinitionId, string? Note = null) : ICommand<PublishResult?>;

    // A pipeline whose graph has a problem that would fail its run - an input its step cannot do without
    // left empty, a value its type cannot hold, a variable that is gone - is not published; the editor shows the same problems
    // on the canvas as they arise (PipelineGraphProblems, one rule). Warnings refuse nothing, and a body the editor did not
    // draw (no flowchart) has no graph to judge. The engine's own validation still follows.
    public sealed class PublishPipelineCommandHandler(IPipelineDefinitionStore definitions, IPipelineDefinitionPublisher publisher, IStepRegistry registry, IRegistriesPopulator populator,
        IKeyValueStore keyValues, ITenantAccessor tenants)
        : ICommandHandler<PublishPipelineCommand, PublishResult?>
    {
        public async Task<PublishResult?> Handle(PublishPipelineCommand request, CancellationToken ct)
        {
            var latest = await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = request.DefinitionId, VersionOptions = VersionOptions.Latest }, ct);
            if (latest is null)
            {
                return null;
            }

            // n8n's archive: an archived pipeline is not put live again until it is restored.
            if (await PipelineArchiveStore.FindAsync(keyValues, tenants.TenantId, request.DefinitionId, ct) is not null)
            {
                return new PublishResult { Succeeded = false, Errors = new List<string> { "The pipeline is archived - restore it to publish it." } };
            }

            if (!registry.ListAll().Any())
            {
                await populator.PopulateAsync(ct);
            }

            var problems = PipelineGraphProblems.Find(PipelineGraphCodec.ToGraph(latest.StringData), ListStepsQueryHandler.Catalog(registry))
                .Where(problem => problem.Severity == PipelineGraphProblems.Error)
                .ToList();
            if (problems.Count > 0)
            {
                return new PublishResult { Succeeded = false, Errors = problems.Select(problem => $"{problem.Step}: {problem.Message}").ToList() };
            }

            // What changed, in the author's words - the version published carries it, or none (never the last one's).
            PipelineWire.StampNote(latest, request.Note);
            var result = await publisher.PublishAsync(latest, ct);
            return new PublishResult
            {
                Succeeded = result.Succeeded,
                Errors = result.ValidationErrors.Select(error => error.Message).ToList(),
            };
        }
    }

    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record RetractPipelineCommand(string DefinitionId) : ICommand<bool>;

    public sealed class RetractPipelineCommandHandler(IPipelineDefinitionPublisher publisher) : ICommandHandler<RetractPipelineCommand, bool>
    {
        public async Task<bool> Handle(RetractPipelineCommand request, CancellationToken ct) =>
            await publisher.RetractAsync(request.DefinitionId, ct) is not null;
    }

    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record DeletePipelineCommand(string DefinitionId) : ICommand<bool>;

    public sealed class DeletePipelineCommandHandler(IPipelineDefinitionManager manager, IKeyValueStore keyValues, ITenantAccessor tenants,
        Comments.IPipelineStepCommentRepository comments, Stomme.SharedKernel.Domain.IUnitOfWork unitOfWork, Stomme.Shared.SiteState siteState)
        : ICommandHandler<DeletePipelineCommand, bool>
    {
        public async Task<bool> Handle(DeletePipelineCommand request, CancellationToken ct)
        {
            if (await manager.DeleteByDefinitionIdAsync(request.DefinitionId, ct) == 0)
            {
                return false;
            }

            // Its tags go with it, its place in the archive and its folder - and the discussion of its steps.
            await PipelineTagStore.SaveAsync(keyValues, tenants.TenantId, request.DefinitionId, [], ct);
            await PipelineArchiveStore.DeleteAsync(keyValues, tenants.TenantId, request.DefinitionId, ct);
            await PipelineFolderStore.SaveAsync(keyValues, tenants.TenantId, request.DefinitionId, "", ct);
            await keyValues.DeleteAsync(Triggers.PipelineRunSettingsTask.StepDataMarkKey(tenants.TenantId, request.DefinitionId), ct); // and how far its runs' step data was emptied
            // and what its Remove records seen before steps have seen
            foreach (var seen in await keyValues.FindManyAsync(new Stomme.Engine.KeyValues.Models.KeyValueFilter { Key = Steps.RemoveSeenRecords.PipelinePrefix(tenants.TenantId, request.DefinitionId), StartsWith = true }, ct))
            {
                await keyValues.DeleteAsync(seen.Key, ct);
            }
            if (siteState.Alias?.SiteId is int siteId && siteId > 0 && await comments.DeleteByPipelineAsync(siteId, request.DefinitionId.Trim(), ct) > 0)
            {
                await unitOfWork.SaveChangesAsync(ct);
            }

            return true;
        }
    }

    /// <summary>
    /// Starts a run of the PUBLISHED version. D111 item 3: the run is dispatched to the engine's background dispatcher
    /// and never awaited here - what comes back is the id the caller polls. The dispatch carries the request's engine
    /// tenant to the worker (the tenant bridge), so the run belongs to the site that started it.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Run)]
    public sealed record StartPipelineCommand(string DefinitionId, string? CorrelationId, IDictionary<string, string>? Input) : ICommand<PipelineRunStarted?>;

    public sealed class StartPipelineCommandHandler(IPipelineDefinitionStore definitions, IPipelineInstanceStore instances, IPipelineDispatcher dispatcher, IIdentityGenerator identities,
        Reliability.PipelineRunStops stops, ITenantAccessor tenants, Stomme.Engine.Common.ISystemClock clock, Reliability.PipelineTurns turns)
        : ICommandHandler<StartPipelineCommand, PipelineRunStarted?>
    {
        public async Task<PipelineRunStarted?> Handle(StartPipelineCommand request, CancellationToken ct)
        {
            var published = await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = request.DefinitionId, VersionOptions = VersionOptions.Published }, ct);
            if (published is null)
            {
                return null;
            }

            // How many runs may go at once - the dispatch is not judged by the engine, so it is asked here.
            var correlationId = string.IsNullOrWhiteSpace(request.CorrelationId) ? null : request.CorrelationId.Trim();
            var input = request.Input?.ToDictionary(pair => pair.Key, pair => (object)pair.Value);
            if (Reliability.PipelineTurns.IsInTurn(published))
            {
                // One at a time, the rest in turn: stored, then dispatched - or waiting its turn.
                return await turns.StartAsync(published.Id, new() { CorrelationId = correlationId, Input = input }, ct);
            }

            if (await PipelineRunPolicy.RefusalAsync(instances, published, correlationId, ct) is { } refusal)
            {
                throw new InvalidOperationException(refusal);
            }

            var runId = identities.GenerateId();
            var response = await dispatcher.DispatchAsync(new DispatchPipelineDefinitionRequest(published.Id)
            {
                InstanceId = runId,
                CorrelationId = correlationId,
                Input = input,
            }, options: null, ct);

            if (response.Fault is not null)
            {
                throw new InvalidOperationException("The run could not be dispatched: " + response.Fault.Message);
            }

            // Known here before the engine stores it - all through its first burst of work - so its Stop reaches it.
            stops.Seen(runId, tenants.TenantId, clock.UtcNow);
            return new PipelineRunStarted { RunId = runId };
        }
    }

    /// <summary>
    /// n8n's "retry": a run started again, with the input and correlation the earlier one had (a webhook's body, a
    /// schedule's time, a failure a handler was told of), on the pipeline's PUBLISHED version - what a run of it is now; the
    /// version the earlier one ran may have been fixed since, which is the point of running it again. Dispatched like Run; null
    /// when the run is not on the site, refused when its pipeline has no published version. <paramref name="OnItsVersion"/>:
    /// n8n's "retry with original workflow" - on the version the earlier run ran instead, to tell a fault the published
    /// version brought from one the run met; refused when that version is no longer kept.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Run)]
    public sealed record RetryRunCommand(string Id, bool OnItsVersion = false) : ICommand<PipelineRunStarted?>;

    public sealed class RetryRunCommandHandler(IPipelineInstanceStore instances, IPipelineDefinitionStore definitions, IPipelineDispatcher dispatcher, IIdentityGenerator identities,
        Reliability.PipelineTurns turns)
        : ICommandHandler<RetryRunCommand, PipelineRunStarted?>
    {
        public async Task<PipelineRunStarted?> Handle(RetryRunCommand request, CancellationToken ct)
        {
            var earlier = await instances.FindAsync(new PipelineInstanceFilter { Id = request.Id }, ct);
            if (earlier is null || string.IsNullOrEmpty(earlier.DefinitionId))
            {
                return null;
            }

            if (KeepRunInput.WasEmptied(earlier.PipelineState))
            {
                throw new InvalidOperationException(KeepRunInput.NotKeptRefusal);
            }

            var version = request.OnItsVersion
                ? await definitions.FindAsync(new PipelineDefinitionFilter { Id = earlier.DefinitionVersionId }, ct) is { } ran && ran.DefinitionId == earlier.DefinitionId
                    ? ran
                    : throw new InvalidOperationException("The version this run ran is not kept any more - run it again on the published version.")
                : await definitions.FindAsync(new PipelineDefinitionFilter { DefinitionId = earlier.DefinitionId, VersionOptions = VersionOptions.Published }, ct)
                    ?? throw new InvalidOperationException("The pipeline has no published version to run again - publish it first.");
            if (Reliability.PipelineTurns.IsInTurn(version))
            {
                return await turns.StartAsync(version.Id, new()
                {
                    CorrelationId = earlier.CorrelationId,
                    Input = KeepRunInput.Of(earlier.PipelineState) is { Count: > 0 } kept ? new Dictionary<string, object>(kept) : null,
                }, ct);
            }

            if (await PipelineRunPolicy.RefusalAsync(instances, version, earlier.CorrelationId, ct) is { } refusal)
            {
                throw new InvalidOperationException(refusal);
            }

            var runId = identities.GenerateId();
            var response = await dispatcher.DispatchAsync(new DispatchPipelineDefinitionRequest(version.Id)
            {
                InstanceId = runId,
                CorrelationId = earlier.CorrelationId,
                Input = KeepRunInput.Of(earlier.PipelineState) is { Count: > 0 } input ? input : null,
            }, options: null, ct);

            if (response.Fault is not null)
            {
                throw new InvalidOperationException("The run could not be dispatched: " + response.Fault.Message);
            }

            return new PipelineRunStarted { RunId = runId };
        }
    }

    /// <summary>
    /// n8n's retry "from the node with the error": a failed run goes on as a new run from the step that failed, on the
    /// pipeline's published version, what had finished not run again (<see cref="PipelineRetryFromFailure"/>). Null when the run
    /// is not on the site; refused, saying why, when it cannot go on - and as Retry is, while the pipeline runs one at a time.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Run)]
    public sealed record RetryFromFailedStepCommand(string Id) : ICommand<PipelineRunStarted?>;

    public sealed class RetryFromFailedStepCommandHandler(IPipelineInstanceStore instances, IPipelineDefinitionService definitions, IPipelineInstanceManager runs,
        IPipelineStateSerializer states, IPipelineDispatcher dispatcher, IIdentityGenerator identities, Stomme.Engine.Common.ISystemClock clock,
        Reliability.PipelineTurns turns)
        : ICommandHandler<RetryFromFailedStepCommand, PipelineRunStarted?>
    {
        public async Task<PipelineRunStarted?> Handle(RetryFromFailedStepCommand request, CancellationToken ct)
        {
            var earlier = await instances.FindAsync(new PipelineInstanceFilter { Id = request.Id }, ct);
            if (earlier is null || string.IsNullOrEmpty(earlier.DefinitionId))
            {
                return null;
            }

            if (KeepRunInput.WasEmptied(earlier.PipelineState))
            {
                throw new InvalidOperationException(KeepRunInput.NotKeptRefusal); // the steps after the failed one could read it
            }

            var published = await definitions.FindPipelineGraphAsync(earlier.DefinitionId, VersionOptions.Published, ct)
                ?? throw new InvalidOperationException("The pipeline has no published version to go on with - publish it first.");
            var runId = identities.GenerateId();
            var state = PipelineRetryFromFailure.Prepare(states.Deserialize(states.Serialize(earlier.PipelineState)), runId, published, clock.UtcNow, identities.GenerateId, out var refusal)
                ?? throw new InvalidOperationException(refusal);
            var definition = await definitions.FindPipelineDefinitionAsync(published.Pipeline.Identity.Id, ct);
            if (definition is not null && Reliability.PipelineTurns.IsInTurn(definition))
            {
                return await turns.StartAsync(state, ct); // goes on now, or waits its turn
            }

            if (definition is not null && await PipelineRunPolicy.RefusalAsync(instances, definition, earlier.CorrelationId, ct) is { } busy)
            {
                throw new InvalidOperationException(busy);
            }

            await runs.CreateAsync(state, ct);
            var response = await dispatcher.DispatchAsync(new DispatchPipelineInstanceRequest(runId), options: null, ct);
            if (response.Fault is not null)
            {
                throw new InvalidOperationException("The run could not be dispatched: " + response.Fault.Message);
            }

            return new PipelineRunStarted { RunId = runId };
        }
    }

    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Run)]
    public sealed record CancelRunCommand(string Id) : ICommand<bool>;

    // A run is stopped where it is - waiting, its stored state cancelled and what it waited on cleared; busy, asked to stop
    // before its next step (PipelineRunStops) - and so is every run it started that is still going, at any depth. Stopped alone,
    // a caller left its sub-pipeline going: one waiting for an approval would have done its work once someone decided (n8n's Stop
    // ends the whole execution, its sub-workflows with it).
    public sealed class CancelRunCommandHandler(IPipelineRuntime runtime, IPipelineInstanceStore instances, Reliability.PipelineRunStops stops,
        Stomme.Engine.Common.ISystemClock clock, ITenantAccessor tenants) : ICommandHandler<CancelRunCommand, bool>
    {
        /// <summary>How deep the runs it started are followed: far past any real chain of calls, and a bound on the stop's work.</summary>
        public const int MaxDepth = 16;

        public async Task<bool> Handle(CancelRunCommand request, CancellationToken ct)
        {
            var client = await runtime.CreateClientAsync(request.Id, ct);
            if (!await client.InstanceExistsAsync(ct))
            {
                // Not stored yet: a run in its first burst of work, which the engine stores when the burst ends - stopped before its
                // next step when this process dispatched it or is working on it, for this site; any other id is no run of its.
                if (!stops.Knows(request.Id, tenants.TenantId, clock.UtcNow))
                {
                    return false;
                }

                stops.Ask(request.Id, clock.UtcNow);
                return true;
            }

            var level = new List<string> { request.Id };
            var seen = new HashSet<string>(level, StringComparer.Ordinal);
            for (var depth = 0; depth < MaxDepth && level.Count > 0; depth++)
            {
                foreach (var id in level)
                {
                    stops.Ask(id, clock.UtcNow); // first: a busy run stops at its next step and does not write its state over the cancel
                    await (await runtime.CreateClientAsync(id, ct)).CancelAsync(ct);
                }

                var started = await instances.FindManyAsync(new PipelineInstanceFilter { ParentPipelineInstanceIds = level, PipelineStatus = PipelineStatus.Running }, ct);
                level = started.Select(run => run.Id).Where(seen.Add).ToList();
            }

            return true;
        }
    }

    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record DeleteRunCommand(string Id) : ICommand<bool>;

    public sealed class DeleteRunCommandHandler(IPipelineInstanceManager instances, IKeyValueStore keyValues, ITenantAccessor tenants) : ICommandHandler<DeleteRunCommand, bool>
    {
        public async Task<bool> Handle(DeleteRunCommand request, CancellationToken ct)
        {
            if (!await instances.DeleteAsync(new PipelineInstanceFilter { Id = request.Id }, ct))
            {
                return false;
            }

            // What it saved about itself goes with it.
            await PipelineRunDataStore.DeleteAsync(keyValues, tenants.TenantId, [request.Id], ct);
            return true;
        }
    }

    internal static class Names
    {
        public const int MaxLength = 200;

        public static string Require(string? name)
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                throw new ArgumentException("A pipeline needs a name.", nameof(name));
            }

            if (trimmed.Length > MaxLength)
            {
                throw new ArgumentException($"A pipeline's name is at most {MaxLength} characters.", nameof(name));
            }

            return trimmed;
        }
    }
}
