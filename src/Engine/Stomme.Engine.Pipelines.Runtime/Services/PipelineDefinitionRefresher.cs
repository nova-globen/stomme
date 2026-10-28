using Stomme.Engine.Common.Entities;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class PipelineDefinitionsRefresher(IPipelineDefinitionStore store, ITriggerIndexer triggerIndexer, INotificationSender notificationSender) : IPipelineDefinitionsRefresher
{
    /// <inheritdoc />
    public async Task<RefreshPipelineDefinitionsResponse> RefreshPipelineDefinitionsAsync(RefreshPipelineDefinitionsRequest request, CancellationToken cancellationToken)
    {
        var filter = new PipelineDefinitionFilter
        {
            DefinitionIds = request.DefinitionIds,
            VersionOptions = VersionOptions.Published
        };

        var currentPage = 0;
        var processedPipelineDefinitions = new List<PipelineDefinition>();
        var batchSize = request.BatchSize;
        var order = new PipelineDefinitionOrder<string>(x => x.Id, OrderDirection.Ascending);

        while (!cancellationToken.IsCancellationRequested)
        {
            var pageArgs = PageArgs.FromPage(currentPage, batchSize);
            var definitions = await store.FindManyAsync(filter, order, pageArgs, cancellationToken).ConfigureAwait(false);

            if (definitions.Items.Count == 0)
                break;

            await IndexPipelineTriggersAsync(definitions.Items, cancellationToken).ConfigureAwait(false);
            processedPipelineDefinitions.AddRange(definitions.Items);
            currentPage++;

            if (definitions.Items.Count < batchSize)
                break;
        }

        var processedPipelineDefinitionIds = processedPipelineDefinitions.Select(x => x.DefinitionId).ToList();
        var notification = new PipelineDefinitionsRefreshed(processedPipelineDefinitionIds);
        await notificationSender.SendAsync(notification, cancellationToken).ConfigureAwait(false);
        return new(processedPipelineDefinitionIds, request.DefinitionIds?.Except(processedPipelineDefinitionIds)?.ToList() ?? []);
    }

    private async Task IndexPipelineTriggersAsync(IEnumerable<PipelineDefinition> definitions, CancellationToken cancellationToken)
    {
        foreach (var definition in definitions)
            await triggerIndexer.IndexTriggersAsync(definition, cancellationToken).ConfigureAwait(false);
    }
}