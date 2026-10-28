using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime.Requests;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class PipelineCancellationService(
    IPipelineDefinitionService pipelineDefinitionService,
    IPipelineInstanceStore pipelineInstanceStore,
    IPipelineCancellationDispatcher dispatcher)
    : IPipelineCancellationService
{
    /// <inheritdoc />
    public async Task<bool> CancelPipelineAsync(string pipelineInstanceId, CancellationToken cancellationToken = default)
    {
        var filter = new PipelineInstanceFilter
        {
            Id = pipelineInstanceId
        };
        var instance = await pipelineInstanceStore.FindAsync(filter, cancellationToken).ConfigureAwait(false);

        if(instance == null)
            return false;
            
        await CancelPipelines([instance], cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> CancelPipelinesAsync(IEnumerable<string> pipelineInstanceIds, CancellationToken cancellationToken = default)
    {
        var filter = new PipelineInstanceFilter
        {
            Ids = pipelineInstanceIds.ToList()
        };
        var instances = await pipelineInstanceStore.FindManyAsync(filter, cancellationToken).ConfigureAwait(false);
        return await CancelPipelines(instances.ToList(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CancelPipelineByDefinitionVersionAsync(string definitionVersionId, CancellationToken cancellationToken = default)
    {
        var filter = new PipelineInstanceFilter
        {
            DefinitionVersionId = definitionVersionId,
            PipelineStatus = PipelineStatus.Running
        };
        var instances = (await pipelineInstanceStore.FindManyAsync(filter, cancellationToken).ConfigureAwait(false)).ToList();
        var instanceIds = instances.Select(i => i.Id).ToList();

        return await CancelPipelinesAsync(instanceIds, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> CancelPipelineByDefinitionAsync(string definitionId, VersionOptions versionOptions, CancellationToken cancellationToken = default)
    {
        // Shouldn't we get possible multiple definitions here?
        var pipelineDefinition = await pipelineDefinitionService.FindPipelineDefinitionAsync(definitionId, versionOptions, cancellationToken).ConfigureAwait(false);
        if (pipelineDefinition is null)
            return 0;

        return await CancelPipelineByDefinitionVersionAsync(pipelineDefinition.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> CancelPipelines(IList<PipelineInstance> pipelineInstances, CancellationToken cancellationToken)
    {
        var tasks = pipelineInstances.Where(i => i.Status != PipelineStatus.Finished)
            .Select(i => dispatcher.DispatchAsync(new DispatchCancelPipelineRequest
            {
                PipelineInstanceId = i.Id
            }, cancellationToken)).ToList();

        var instanceIds = pipelineInstances.Select(i => i.Id).ToList();
        await CancelChildPipelineInstances(instanceIds, cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(tasks).ConfigureAwait(false);

        return tasks.Count;
    }

    private async Task CancelChildPipelineInstances(IEnumerable<string> pipelineInstanceIds, CancellationToken cancellationToken)
    {
        var tasks = new List<Task<int>>();
        var pipelineInstanceIdBatches = pipelineInstanceIds.Chunk(50);

        foreach (var pipelineInstanceIdBatch in pipelineInstanceIdBatches)
        {
            // Find child instances for the current pipeline instance ID and cancel them.
            // Do not check on status as their children might still be running and need to be canceled.
            PipelineInstanceFilter filter = new()
            {
                ParentPipelineInstanceIds = pipelineInstanceIdBatch.ToList()
            };
            var childInstances = (await pipelineInstanceStore.FindManyAsync(filter, cancellationToken).ConfigureAwait(false)).ToList();

            if (childInstances.Any())
                tasks.Add(CancelPipelines(childInstances, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}