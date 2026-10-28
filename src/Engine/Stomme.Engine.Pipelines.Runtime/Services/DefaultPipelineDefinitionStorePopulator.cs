using Stomme.Engine.Common;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Microsoft.Extensions.Logging;
using Open.Linq.AsyncExtensions;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class DefaultPipelineDefinitionStorePopulator : IPipelineDefinitionStorePopulator
{
    private readonly Func<IEnumerable<IPipelinesProvider>> _pipelineDefinitionProviders;
    private readonly ITriggerIndexer _triggerIndexer;
    private readonly IPipelineDefinitionStore _pipelineDefinitionStore;
    private readonly IStepSerializer _stepSerializer;
    private readonly IPayloadSerializer _payloadSerializer;
    private readonly ISystemClock _systemClock;
    private readonly IIdentityGraphService _identityGraphService;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly ILogger<DefaultPipelineDefinitionStorePopulator> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultPipelineDefinitionStorePopulator"/> class.
    /// </summary>
    public DefaultPipelineDefinitionStorePopulator(
        Func<IEnumerable<IPipelinesProvider>> pipelineDefinitionProviders,
        ITriggerIndexer triggerIndexer,
        IPipelineDefinitionStore pipelineDefinitionStore,
        IStepSerializer stepSerializer,
        IPayloadSerializer payloadSerializer,
        ISystemClock systemClock,
        IIdentityGraphService identityGraphService,
        ITenantAccessor tenantAccessor,
        ILogger<DefaultPipelineDefinitionStorePopulator> logger)
    {
        _pipelineDefinitionProviders = pipelineDefinitionProviders;
        _triggerIndexer = triggerIndexer;
        _pipelineDefinitionStore = pipelineDefinitionStore;
        _stepSerializer = stepSerializer;
        _payloadSerializer = payloadSerializer;
        _systemClock = systemClock;
        _identityGraphService = identityGraphService;
        _tenantAccessor = tenantAccessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<IEnumerable<PipelineDefinition>> PopulateStoreAsync(CancellationToken cancellationToken = default)
    {
        return PopulateStoreAsync(true, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<PipelineDefinition>> PopulateStoreAsync(bool indexTriggers, CancellationToken cancellationToken = default)
    {
        var providers = _pipelineDefinitionProviders();
        var pipelineDefinitions = new List<PipelineDefinition>();
        var currentTenantId = _tenantAccessor.TenantId;

        foreach (var provider in providers)
        {
            var results = await provider.GetPipelinesAsync(cancellationToken).AsTask().ToList().ConfigureAwait(false);

            foreach (var result in results)
            {
                // Normalize tenant IDs for comparison (null becomes empty string)
                var definitionTenantId = result.Pipeline.Identity.TenantId.NormalizeTenantId();

                // Only import pipelines belonging to the current tenant or tenant-agnostic pipelines (TenantId = "*").
                if (definitionTenantId != currentTenantId && definitionTenantId != Tenant.AgnosticTenantId)
                {
                    _logger.LogDebug(
                        "Skipping adding pipeline {PipelineId} from provider {Provider} because it belongs to tenant '{PipelineTenantId}' but current tenant is '{CurrentTenantId}'",
                        result.Pipeline.Identity.DefinitionId,
                        provider.Name,
                        result.Pipeline.Identity.TenantId,
                        currentTenantId);
                    continue;
                }

                var addResult = await AddAsync(result, indexTriggers, cancellationToken).ConfigureAwait(false);
                addResult.OnSuccess(pipelineDefinition => pipelineDefinitions.Add(pipelineDefinition));
            }
        }

        return pipelineDefinitions;
    }

    /// <inheritdoc />
    public Task<Result<PipelineDefinition>> AddAsync(MaterializedPipeline materializedPipeline, CancellationToken cancellationToken = default)
    {
        return AddAsync(materializedPipeline, true, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<PipelineDefinition>> AddAsync(MaterializedPipeline materializedPipeline, bool indexTriggers, CancellationToken cancellationToken = default)
    {
        await AssignIdentities(materializedPipeline.Pipeline, cancellationToken).ConfigureAwait(false);
        var result = await AddOrUpdateAsync(materializedPipeline, cancellationToken).ConfigureAwait(false);

        return await result.OnSuccessAsync(async pipelineDefinition =>
        {
            if (indexTriggers && pipelineDefinition.IsPublished)
                await _triggerIndexer.IndexTriggersAsync(pipelineDefinition, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task AssignIdentities(Pipeline pipeline, CancellationToken cancellationToken)
    {
        await _identityGraphService.AssignIdentitiesAsync(pipeline, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<PipelineDefinition>> AddOrUpdateAsync(MaterializedPipeline materializedPipeline, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var pipelineDefinition = await AddOrUpdateCoreAsync(materializedPipeline, cancellationToken).ConfigureAwait(false);
            return Result.Success(pipelineDefinition);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "An error occurred while populating the pipeline definition store for pipeline {PipelineId} (Name: {PipelineName}, Version: {Version})",
                materializedPipeline.Pipeline.Identity.DefinitionId,
                materializedPipeline.Pipeline.PipelineMetadata.Name,
                materializedPipeline.Pipeline.Identity.Version);

            return Result.Failure<PipelineDefinition>(e);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<PipelineDefinition> AddOrUpdateCoreAsync(MaterializedPipeline materializedPipeline, CancellationToken cancellationToken = default)
    {
        var pipeline = materializedPipeline.Pipeline;
        var definitionId = pipeline.Identity.DefinitionId;

        var existingPipelineLatest = false;
        var existingPipelinePublished = false;

        // Serialize materializer context.
        var materializerContext = materializedPipeline.MaterializerContext;
        var materializerContextJson = materializerContext != null ? _payloadSerializer.Serialize(materializerContext) : null;

        // Determine StringData and OriginalSource based on what's provided
        string? stringData;
        var originalSource = materializedPipeline.OriginalSource;

        if (originalSource != null)
        {
            // NEW WAY: OriginalSource is provided
            // For JSON pipelines, we still need to populate StringData with the serialized root for backwards compatibility
            stringData = materializedPipeline.MaterializerName == "Json"
                ? _stepSerializer.Serialize(pipeline.Root)
                :
                // For new formats (EngineScript, YAML, etc.), only OriginalSource is needed
                // StringData can be null as these materializers only use OriginalSource
                null;
        }
        else
        {
            // OLD WAY: No OriginalSource provided (backwards compatibility for existing pipelines)
            // Serialize the pipeline root as before
            stringData = _stepSerializer.Serialize(pipeline.Root);
        }

        // Check if there's already a pipeline definition stored with this definition ID and version.
        var specificVersionFilter = new PipelineDefinitionFilter
        {
            DefinitionId = definitionId,
            VersionOptions = VersionOptions.SpecificVersion(pipeline.Identity.Version)
        };

        var existingDefinitionVersion = await _pipelineDefinitionStore.FindAsync(specificVersionFilter, cancellationToken).ConfigureAwait(false);

        // Set up a list to collect all pipeline definitions to be persisted.
        var pipelineDefinitionsToSave = new HashSet<PipelineDefinition>();

        if (existingDefinitionVersion != null)
        {
            pipelineDefinitionsToSave.Add(existingDefinitionVersion);

            if (existingDefinitionVersion.Id != pipeline.Identity.Id)
            {
                // It's possible that the imported pipeline definition has a different ID than the existing one in the store.
                // In a future update, we might store this discrepancy in a "troubleshooting" table and provide tooling for managing these, and other, discrepancies.
                // See https://github.com/elsa-workflows/elsa-core/issues/5540
                _logger.LogWarning("Pipeline with ID {PipelineId} already exists with a different ID {ExistingPipelineId}", pipeline.Identity.Id, existingDefinitionVersion.Id);
            }
        }

        await UpdateIsLatest().ConfigureAwait(false);
        await UpdateIsPublished().ConfigureAwait(false);

        // Determine the tenant ID for the pipeline definition
        // If the pipeline has no tenant ID, use the current tenant (normalized to handle null -> "*")
        var pipelineTenantId = pipeline.Identity.TenantId ?? _tenantAccessor.TenantId;

        var pipelineDefinition = existingDefinitionVersion ?? new PipelineDefinition
        {
            DefinitionId = pipeline.Identity.DefinitionId,
            Id = pipeline.Identity.Id,
            Version = pipeline.Identity.Version,
            TenantId = pipelineTenantId,
        };
        
        pipelineDefinition.Description = pipeline.PipelineMetadata.Description;
        pipelineDefinition.Name = pipeline.PipelineMetadata.Name;
        pipelineDefinition.ToolVersion = pipeline.PipelineMetadata.ToolVersion;
        pipelineDefinition.IsLatest = !existingPipelineLatest;
        pipelineDefinition.IsPublished = !existingPipelinePublished && pipeline.Publication.IsPublished;
        pipelineDefinition.IsReadonly = pipeline.IsReadonly;
        pipelineDefinition.IsSystem = pipeline.IsSystem;
        pipelineDefinition.CustomProperties = pipeline.CustomProperties;
        pipelineDefinition.Variables = pipeline.Variables;
        pipelineDefinition.Inputs = pipeline.Inputs;
        pipelineDefinition.Outputs = pipeline.Outputs;
        pipelineDefinition.Outcomes = pipeline.Outcomes;
        pipelineDefinition.StringData = stringData;
        pipelineDefinition.OriginalSource = originalSource;
        pipelineDefinition.CreatedAt = pipeline.PipelineMetadata.CreatedAt == default ? _systemClock.UtcNow : pipeline.PipelineMetadata.CreatedAt;
        pipelineDefinition.Options = pipeline.Options;
        pipelineDefinition.ProviderName = materializedPipeline.ProviderName;
        pipelineDefinition.MaterializerContext = materializerContextJson;
        pipelineDefinition.MaterializerName = materializedPipeline.MaterializerName;

        if (existingDefinitionVersion is null && pipelineDefinitionsToSave.Any(w => w.Id == pipelineDefinition.Id))
        {
            _logger.LogInformation("Pipeline with ID {PipelineId} already exists", pipelineDefinition.Id);
            return pipelineDefinition;
        }

        pipelineDefinitionsToSave.Add(pipelineDefinition);

        var duplicates = pipelineDefinitionsToSave.GroupBy(wd => wd.Id)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Any())
        {
            throw new Exception($"Unable to update PipelineDefinition with ids {string.Join(',', duplicates)} multiple times.");
        }

        await _pipelineDefinitionStore.SaveManyAsync(pipelineDefinitionsToSave, cancellationToken).ConfigureAwait(false);
        return pipelineDefinition;

        async Task UpdateIsLatest()
        {
            // Always try to update the IsLatest property based on the VersionNumber

            // Reset current latest definitions.
            var filter = new PipelineDefinitionFilter
            {
                DefinitionId = definitionId,
                VersionOptions = VersionOptions.Latest
            };
            var latestPipelineDefinitions = (await _pipelineDefinitionStore.FindManyAsync(filter, cancellationToken).ConfigureAwait(false)).ToList();

            // If the latest definitions contains definitions with the same ID then we need to replace them with the latest pipeline definitions.
            SyncExistingCopies(latestPipelineDefinitions, pipelineDefinitionsToSave);

            foreach (var latestPipelineDefinition in latestPipelineDefinitions)
            {
                if (latestPipelineDefinition.Version > pipeline.Identity.Version)
                {
                    _logger.LogWarning("A more recent version of the pipeline has been found, overwriting the IsLatest property on the pipeline");
                    existingPipelineLatest = true;
                    continue;
                }

                latestPipelineDefinition.IsLatest = false;
                pipelineDefinitionsToSave.Add(latestPipelineDefinition);
            }
        }

        async Task UpdateIsPublished()
        {
            // If the pipeline being added is configured to be the published version, then we need to reset the current published version.
            if (pipeline.Publication.IsPublished)
            {
                // Reset current published definitions.
                var filter = new PipelineDefinitionFilter
                {
                    DefinitionId = definitionId,
                    VersionOptions = VersionOptions.Published
                };
                var publishedPipelineDefinitions = (await _pipelineDefinitionStore.FindManyAsync(filter, cancellationToken).ConfigureAwait(false)).ToList();

                // If the published pipeline definitions contains definitions with the same ID as definitions in the latest pipeline definitions, then we need to replace them with the latest pipeline definitions.
                SyncExistingCopies(publishedPipelineDefinitions, pipelineDefinitionsToSave);

                foreach (var publishedPipelineDefinition in publishedPipelineDefinitions)
                {
                    if (publishedPipelineDefinition.Version > pipeline.Identity.Version)
                    {
                        _logger.LogWarning("A more recent version of the pipeline has been found to be published, overwriting the IsPublished property on the pipeline");
                        existingPipelinePublished = true;
                        continue;
                    }

                    publishedPipelineDefinition.IsPublished = false;
                    pipelineDefinitionsToSave.Add(publishedPipelineDefinition);
                }
            }
        }
    }

    /// <summary>
    /// Syncs the items in the primary list with existing items in the secondary list, even when the object instances are not the same (but their IDs are).
    /// </summary>
    private void SyncExistingCopies(List<PipelineDefinition> primary, HashSet<PipelineDefinition> secondary)
    {
        var ids = secondary.Select(x => x.Id).Distinct().ToList();
        var latestPipelineDefinitions = primary.Where(x => ids.Contains(x.Id)).ToList();
        primary.RemoveAll(x => latestPipelineDefinitions.Contains(x));
        primary.AddRange(secondary.Where(x => ids.Contains(x.Id)));
    }
}