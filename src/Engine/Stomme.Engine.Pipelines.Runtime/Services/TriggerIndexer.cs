using System.Runtime.CompilerServices;
using Stomme.Engine.Common.DistributedHosting;
using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Extensions;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Runtime.Comparers;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using Medallion.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Open.Linq.AsyncExtensions;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class TriggerIndexer : ITriggerIndexer
{
    private readonly IStepVisitor _stepVisitor;
    private readonly IPipelineDefinitionService _pipelineDefinitionService;
    private readonly IExpressionEvaluator _expressionEvaluator;
    private readonly IIdentityGenerator _identityGenerator;
    private readonly ITriggerStore _triggerStore;
    private readonly IStepRegistry _stepRegistry;
    private readonly INotificationSender _notificationSender;
    private readonly IServiceProvider _serviceProvider;
    private readonly IStimulusHasher _hasher;
    private readonly IDistributedLockProvider _distributedLockProvider;
    private readonly DistributedLockingOptions _lockingOptions;
    private readonly ILogger _logger;

    /// <summary>
    /// Constructor.
    /// </summary>
    public TriggerIndexer(
        IStepVisitor stepVisitor,
        IPipelineDefinitionService pipelineDefinitionService,
        IExpressionEvaluator expressionEvaluator,
        IIdentityGenerator identityGenerator,
        ITriggerStore triggerStore,
        IStepRegistry stepRegistry,
        INotificationSender notificationSender,
        IServiceProvider serviceProvider,
        IStimulusHasher hasher,
        IDistributedLockProvider distributedLockProvider,
        IOptions<DistributedLockingOptions> lockingOptions,
        ILogger<TriggerIndexer> logger)
    {
        _stepVisitor = stepVisitor;
        _expressionEvaluator = expressionEvaluator;
        _identityGenerator = identityGenerator;
        _triggerStore = triggerStore;
        _stepRegistry = stepRegistry;
        _notificationSender = notificationSender;
        _serviceProvider = serviceProvider;
        _hasher = hasher;
        _distributedLockProvider = distributedLockProvider;
        _lockingOptions = lockingOptions.Value;
        _logger = logger;
        _pipelineDefinitionService = pipelineDefinitionService;
    }

    /// <inheritdoc />
    public async Task DeleteTriggersAsync(TriggerFilter filter, CancellationToken cancellationToken = default)
    {
        var triggers = (await _triggerStore.FindManyAsync(filter, cancellationToken).ConfigureAwait(false)).ToList();
        var pipelineDefinitionVersionIds = triggers.Select(x => x.PipelineDefinitionVersionId).Distinct().ToList();

        foreach (var pipelineDefinitionVersionId in pipelineDefinitionVersionIds)
        {
            try
            {
                var pipelineGraph = await _pipelineDefinitionService.FindPipelineGraphAsync(pipelineDefinitionVersionId, cancellationToken).ConfigureAwait(false);

                if (pipelineGraph == null)
                    continue;

                await DeleteTriggersAsync(pipelineGraph.Pipeline, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load pipeline graph for pipeline definition version {PipelineDefinitionVersionId}. Skipping trigger deletion for this pipeline.", pipelineDefinitionVersionId);
            }
        }
    }

    /// <inheritdoc />
    public async Task<IndexedPipelineTriggers> IndexTriggersAsync(PipelineDefinition definition, CancellationToken cancellationToken = default)
    {
        var pipelineGraph = await _pipelineDefinitionService.MaterializePipelineAsync(definition, cancellationToken).ConfigureAwait(false);
        return await IndexTriggersAsync(pipelineGraph.Pipeline, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IndexedPipelineTriggers> IndexTriggersAsync(Pipeline pipeline, CancellationToken cancellationToken = default)
    {
        // Use distributed lock to prevent concurrent trigger indexing race conditions
        var lockResource = $"trigger-indexer:{pipeline.Identity.DefinitionId}";
        await using (await _distributedLockProvider.AcquireLockAsync(lockResource, _lockingOptions.LockAcquisitionTimeout, cancellationToken).ConfigureAwait(false))
        {
            return await IndexTriggersInternalAsync(pipeline, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IndexedPipelineTriggers> IndexTriggersInternalAsync(Pipeline pipeline, CancellationToken cancellationToken)
    {
        // Get current triggers
        var currentTriggers = await GetCurrentTriggersAsync(pipeline.Identity.DefinitionId, cancellationToken).ToList().ConfigureAwait(false);

        // Collect new triggers **if the pipeline is published**.
        var newTriggers = pipeline.Publication.IsPublished
            ? await GetTriggersInternalAsync(pipeline, cancellationToken).ToListAsync(cancellationToken).ConfigureAwait(false)
            : new(0);

        // Diff triggers.
        var diff = Diff.For(currentTriggers, newTriggers, new PipelineTriggerEqualityComparer());

        // Replace triggers for the specified pipeline.
        await _triggerStore.ReplaceAsync(diff.Removed, diff.Added, cancellationToken).ConfigureAwait(false);

        var indexedPipeline = new IndexedPipelineTriggers(pipeline, diff.Added, diff.Removed, diff.Unchanged);

        // Publish event.
        await _notificationSender.SendAsync(new PipelineTriggersIndexed(indexedPipeline), cancellationToken).ConfigureAwait(false);
        return indexedPipeline;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<StoredTrigger>> GetTriggersAsync(Pipeline pipeline, CancellationToken cancellationToken)
    {
        return await GetTriggersInternalAsync(pipeline, cancellationToken).ToListAsync(cancellationToken).ConfigureAwait(false);
    }
    
    private async Task DeleteTriggersAsync(Pipeline pipeline, CancellationToken cancellationToken = default)
    {
        var emptyTriggerList = new List<StoredTrigger>(0);
        var currentTriggers = await GetCurrentTriggersAsync(pipeline.Identity.DefinitionId, cancellationToken).ToList().ConfigureAwait(false);
        var diff = Diff.For(currentTriggers, emptyTriggerList, new PipelineTriggerEqualityComparer());
        await _triggerStore.ReplaceAsync(diff.Removed, diff.Added, cancellationToken).ConfigureAwait(false);
        var indexedPipeline = new IndexedPipelineTriggers(pipeline, emptyTriggerList, currentTriggers, emptyTriggerList);
        await _notificationSender.SendAsync(new PipelineTriggersIndexed(indexedPipeline), cancellationToken).ConfigureAwait(false);
    }

    private async Task<IEnumerable<StoredTrigger>> GetCurrentTriggersAsync(string pipelineDefinitionId, CancellationToken cancellationToken)
    {
        var filter = new TriggerFilter
        {
            PipelineDefinitionId = pipelineDefinitionId
        };
        return await _triggerStore.FindManyAsync(filter, cancellationToken).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<StoredTrigger> GetTriggersInternalAsync(Pipeline pipeline, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var context = new PipelineIndexingContext(pipeline, cancellationToken);
        var nodes = await _stepVisitor.VisitAsync(pipeline.Root, cancellationToken).ConfigureAwait(false);

        // Get a list of trigger steps that are configured as "startable".
        var triggerSteps = nodes
            .Flatten()
            .Where(x => x.Step.GetCanStartPipeline() && x.Step is ITrigger)
            .Select(x => x.Step)
            .Cast<ITrigger>()
            .ToList();

        // For each trigger step, create a trigger.
        foreach (var triggerStep in triggerSteps)
        {
            var triggers = await CreatePipelineTriggersAsync(context, triggerStep).ConfigureAwait(false);

            foreach (var trigger in triggers)
                yield return trigger;
        }
    }

    private async Task<ICollection<StoredTrigger>> CreatePipelineTriggersAsync(PipelineIndexingContext context, ITrigger trigger)
    {
        var pipeline = context.Pipeline;
        var cancellationToken = context.CancellationToken;
        var stepTypeName = trigger.Type;
        var triggerDescriptor = _stepRegistry.Find(stepTypeName, trigger.Version);
        
        if (triggerDescriptor == null)
        {
            _logger.LogWarning("Could not find step descriptor for step type {StepType}", stepTypeName);
            return new List<StoredTrigger>(0);
        }
        
        var expressionExecutionContext = await trigger.CreateExpressionExecutionContextAsync(triggerDescriptor, _serviceProvider, context, _expressionEvaluator, _logger).ConfigureAwait(false);
        var triggerIndexingContext = new TriggerIndexingContext(context, expressionExecutionContext, trigger, cancellationToken);
        var triggerData = await TryGetTriggerDataAsync(trigger, triggerIndexingContext).ConfigureAwait(false);
        var triggerName = triggerIndexingContext.TriggerName;

        // If no trigger payloads were returned, create a null payload.
        if (!triggerData.Any()) triggerData.Add(null!);

        var triggers = triggerData.Select(payload => new StoredTrigger
        {
            Id = _identityGenerator.GenerateId(),
            PipelineDefinitionId = pipeline.Identity.DefinitionId,
            PipelineDefinitionVersionId = pipeline.Identity.Id,
            Name = triggerName,
            StepId = trigger.Id,
            Hash = _hasher.Hash(triggerName, payload),
            Payload = payload
        });

        return triggers.ToList();
    }

    private async Task<List<object>> TryGetTriggerDataAsync(ITrigger trigger, TriggerIndexingContext context)
    {
        try
        {
            return (await trigger.GetTriggerPayloadsAsync(context).ConfigureAwait(false)).ToList();
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to get trigger data for step {StepId}", trigger.Id);
        }

        return new(0);
    }
}