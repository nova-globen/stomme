using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class TriggerBoundPipelineService(IPipelineMatcher pipelineMatcher, IPipelineDefinitionService pipelineDefinitionService, ILogger<TriggerBoundPipelineService> logger) : ITriggerBoundPipelineService
{
    /// <inheritdoc />
    public async Task<IEnumerable<TriggerBoundPipeline>> FindManyAsync(string stepTypeName, object stimulus, CancellationToken cancellationToken = default)
    {
        var triggers = await pipelineMatcher.FindTriggersAsync(stepTypeName, stimulus, cancellationToken).ConfigureAwait(false);
        return await FindManyAsync(triggers, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<TriggerBoundPipeline>> FindManyAsync(string stimulusHash, CancellationToken cancellationToken = default)
    {
        var triggers = await pipelineMatcher.FindTriggersAsync(stimulusHash, cancellationToken).ConfigureAwait(false);
        return await FindManyAsync(triggers, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IEnumerable<TriggerBoundPipeline>> FindManyAsync(IEnumerable<StoredTrigger> triggers, CancellationToken cancellationToken = default)
    {
        var groupedTriggers = triggers.GroupBy(x => x.PipelineDefinitionVersionId);
        var triggerBoundPipelines = new List<TriggerBoundPipeline>();

        foreach (var triggerGroup in groupedTriggers)
        {
            var pipelineDefinitionVersionId = triggerGroup.Key;
            var pipelineGraph = await pipelineDefinitionService.FindPipelineGraphAsync(pipelineDefinitionVersionId, cancellationToken).ConfigureAwait(false);

            if (pipelineGraph == null)
            {
                logger.LogWarning("Pipeline definition with ID {PipelineDefinitionVersionId} not found", pipelineDefinitionVersionId);
                continue;
            }

            var triggerBoundPipeline = new TriggerBoundPipeline(pipelineGraph, triggerGroup.ToList());
            triggerBoundPipelines.Add(triggerBoundPipeline);
        }

        return triggerBoundPipelines;
    }
}