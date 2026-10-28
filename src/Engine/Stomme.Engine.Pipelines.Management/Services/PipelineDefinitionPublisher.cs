using Stomme.Engine.Common;
using Stomme.Engine.Common.Entities;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Materializers;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Management.Notifications;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Management.Services;

/// <inheritdoc />
public class PipelineDefinitionPublisher(
    IPipelineDefinitionService pipelineDefinitionService,
    IPipelineDefinitionStore pipelineDefinitionStore,
    IPipelineValidator pipelineValidator,
    IIdentityGenerator identityGenerator,
    IStepSerializer stepSerializer,
    IMediator mediator,
    ISystemClock systemClock)
    : IPipelineDefinitionPublisher
{
    /// <inheritdoc />
    public PipelineDefinition New(IStep? root = null)
    {
        root ??= new Sequence();
        var id = identityGenerator.GenerateId();
        var definitionId = identityGenerator.GenerateId();
        const int version = 1;

        return new()
        {
            Id = id,
            DefinitionId = definitionId,
            Version = version,
            IsLatest = true,
            IsPublished = false,
            CreatedAt = systemClock.UtcNow,
            StringData = stepSerializer.Serialize(root),
            MaterializerName = JsonPipelineMaterializer.MaterializerName
        };
    }

    public Task<PipelineDefinition> NewAsync(IStep? root = null, CancellationToken cancellationToken = default)
    {
        root ??= new Sequence();
        var id = identityGenerator.GenerateId();
        var definitionId = identityGenerator.GenerateId();
        const int version = 1;

        var pipelineDefinition = new PipelineDefinition
        {
            Id = id,
            DefinitionId = definitionId,
            Version = version,
            IsLatest = true,
            IsPublished = false,
            CreatedAt = systemClock.UtcNow,
            StringData = stepSerializer.Serialize(root),
            MaterializerName = JsonPipelineMaterializer.MaterializerName
        };

        return Task.FromResult(pipelineDefinition);
    }

    /// <inheritdoc />
    public async Task<PublishPipelineDefinitionResult> PublishAsync(string definitionId, CancellationToken cancellationToken = default)
    {
        var filter = PipelineDefinitionHandle.ByDefinitionId(definitionId, VersionOptions.Latest).ToFilter();
        var definition = await pipelineDefinitionStore.FindAsync(filter, cancellationToken).ConfigureAwait(false);

        if (definition == null)
            return new(false, new List<PipelineValidationError>
            {
                new("Pipeline definition not found.")
            }, new([]));

        return await PublishAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PublishPipelineDefinitionResult> PublishAsync(PipelineDefinition definition, CancellationToken cancellationToken = default)
    {
        var pipelineGraph = await pipelineDefinitionService.MaterializePipelineAsync(definition, cancellationToken).ConfigureAwait(false);
        var validationErrors = (await pipelineValidator.ValidateAsync(pipelineGraph.Pipeline, cancellationToken).ConfigureAwait(false)).ToList();

        if (validationErrors.Any())
            return new(false, validationErrors, new([]));

        await mediator.SendAsync(new PipelineDefinitionPublishing(definition), cancellationToken).ConfigureAwait(false);
        var definitionId = definition.DefinitionId;

        // Reset current latest and published definitions.
        var publishedPipelines = await pipelineDefinitionStore.FindManyAsync(new()
        {
            DefinitionId = definitionId,
            VersionOptions = VersionOptions.LatestOrPublished
        }, cancellationToken).ConfigureAwait(false);

        foreach (var publishedAndOrLatestPipeline in publishedPipelines)
        {
            var isPublished = publishedAndOrLatestPipeline.IsPublished;
            publishedAndOrLatestPipeline.IsPublished = false;
            publishedAndOrLatestPipeline.IsLatest = false;
            await pipelineDefinitionStore.SaveAsync(publishedAndOrLatestPipeline, cancellationToken).ConfigureAwait(false);

            if (isPublished)
                await mediator.SendAsync(new PipelineDefinitionVersionRetracted(publishedAndOrLatestPipeline), cancellationToken).ConfigureAwait(false);
        }

        // Save the newly published definition.
        definition.IsPublished = true;
        definition.IsLatest = true;
        definition = Initialize(definition);
        await pipelineDefinitionStore.SaveAsync(definition, cancellationToken).ConfigureAwait(false);

        var affectedPipelines = new AffectedPipelines(new List<PipelineDefinition>());
        await mediator.SendAsync(new PipelineDefinitionPublished(definition, affectedPipelines), cancellationToken).ConfigureAwait(false);
        return new(true, validationErrors, affectedPipelines);
    }

    /// <inheritdoc />
    public async Task<PipelineDefinition?> RetractAsync(string definitionId, CancellationToken cancellationToken = default)
    {
        var filter = PipelineDefinitionHandle.ByDefinitionId(definitionId, VersionOptions.Published).ToFilter();
        var definition = await pipelineDefinitionStore.FindAsync(filter, cancellationToken).ConfigureAwait(false);

        if (definition == null)
            return null;

        return await RetractAsync(definition, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PipelineDefinition> RetractAsync(PipelineDefinition definition, CancellationToken cancellationToken = default)
    {
        if (!definition.IsPublished)
            throw new InvalidOperationException("Cannot retract an unpublished pipeline definition.");

        definition.IsPublished = false;

        await mediator.SendAsync(new PipelineDefinitionRetracting(definition), cancellationToken).ConfigureAwait(false);
        await pipelineDefinitionStore.SaveAsync(definition, cancellationToken).ConfigureAwait(false);
        await mediator.SendAsync(new PipelineDefinitionRetracted(definition), cancellationToken).ConfigureAwait(false);
        return definition;
    }

    public async Task<PipelineDefinition> RevertVersionAsync(string definitionId, int version, CancellationToken cancellationToken = default)
    {
        var filter = new PipelineDefinitionFilter
        {
            DefinitionId = definitionId,
            VersionOptions = VersionOptions.Latest
        };
        var latestVersion = await pipelineDefinitionStore.FindAsync(filter, cancellationToken).ConfigureAwait(false);

        if (latestVersion != null)
        {
            latestVersion.IsLatest = false;
            await pipelineDefinitionStore.SaveAsync(latestVersion, cancellationToken).ConfigureAwait(false);
        }

        var draft = await GetDraftAsync(definitionId, VersionOptions.SpecificVersion(version), cancellationToken).ConfigureAwait(false);
        draft!.Id = identityGenerator.GenerateId();
        draft.Version = (latestVersion?.Version ?? 0) + 1;
        draft.IsLatest = true;

        await pipelineDefinitionStore.SaveAsync(draft, cancellationToken).ConfigureAwait(false);
        return draft;
    }

    /// <inheritdoc />
    public async Task<PipelineDefinition?> GetDraftAsync(string definitionId, VersionOptions versionOptions, CancellationToken cancellationToken = default)
    {
        var filter = new PipelineDefinitionFilter
        {
            DefinitionId = definitionId,
            VersionOptions = versionOptions
        };
        var order = new PipelineDefinitionOrder<int>(x => x.Version, OrderDirection.Descending);
        var lastVersion = await pipelineDefinitionStore.FindLastVersionAsync(new()
        {
            DefinitionId = definitionId
        }, cancellationToken).ConfigureAwait(false);
        var definition = await pipelineDefinitionStore.FindAsync(filter, order, cancellationToken).ConfigureAwait(false) ?? lastVersion;

        if (definition == null!)
            return null;

        if (!definition.IsPublished)
            return definition;

        var draft = definition.ShallowClone();

        draft.Version = lastVersion?.Version + 1 ?? 1;
        draft.CreatedAt = systemClock.UtcNow;
        draft.Id = identityGenerator.GenerateId();
        draft.IsLatest = true;
        draft.IsPublished = false;

        return draft;
    }

    /// <inheritdoc />
    public async Task<PipelineDefinition> SaveDraftAsync(PipelineDefinition definition, CancellationToken cancellationToken = default)
    {
        var draft = definition;
        var definitionId = definition.DefinitionId;
        var filter = new PipelineDefinitionFilter
        {
            DefinitionId = definitionId
        };
        var lastVersion = await pipelineDefinitionStore.FindLastVersionAsync(filter, cancellationToken).ConfigureAwait(false);

        draft.Version = draft.Id == lastVersion?.Id ? lastVersion.Version : lastVersion?.Version + 1 ?? 1;
        draft.IsLatest = true;
        draft = Initialize(draft);

        await mediator.SendAsync(new PipelineDefinitionDraftSaving(draft), cancellationToken).ConfigureAwait(false);
        await pipelineDefinitionStore.SaveAsync(draft, cancellationToken).ConfigureAwait(false);
        await mediator.SendAsync(new PipelineDefinitionDraftSaved(draft), cancellationToken).ConfigureAwait(false);

        if (lastVersion is null)
            await mediator.SendAsync(new PipelineDefinitionCreated(definition), cancellationToken).ConfigureAwait(false);

        if (lastVersion is { IsPublished: true, IsLatest: true })
        {
            lastVersion.IsLatest = false;
            await pipelineDefinitionStore.SaveAsync(lastVersion, cancellationToken).ConfigureAwait(false);
        }

        return draft;
    }

    private PipelineDefinition Initialize(PipelineDefinition definition)
    {
        if (definition.Id == null!)
            definition.Id = identityGenerator.GenerateId();

        if (definition.DefinitionId == null!)
            definition.DefinitionId = identityGenerator.GenerateId();

        if (definition.Version == 0)
            definition.Version = 1;

        return definition;
    }
}