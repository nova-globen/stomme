using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Notifications;
using Stomme.Engine.Pipelines.Runtime.Filters;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Handlers;

/// <summary>
/// Deletes bookmarks for pipeline definitions being deleted.
/// </summary>
[UsedImplicitly]
public class DeleteTriggers(ITriggerIndexer triggerIndexer) :
    INotificationHandler<PipelineDefinitionDeleting>,
    INotificationHandler<PipelineDefinitionVersionDeleting>,
    INotificationHandler<PipelineDefinitionsDeleting>,
    INotificationHandler<PipelineDefinitionVersionsDeleting>
{
    /// <inheritdoc />
    public async Task HandleAsync(PipelineDefinitionDeleting notification, CancellationToken cancellationToken)
    {
        await DeleteTriggersAsync(new TriggerFilter
        {
            PipelineDefinitionId = notification.DefinitionId
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task HandleAsync(PipelineDefinitionVersionDeleting notification, CancellationToken cancellationToken)
    {
        await DeleteTriggersAsync(new TriggerFilter
        {
            PipelineDefinitionVersionId = notification.PipelineDefinition.Id
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task HandleAsync(PipelineDefinitionsDeleting notification, CancellationToken cancellationToken)
    {
        await DeleteTriggersAsync(new TriggerFilter
        {
            PipelineDefinitionIds = notification.DefinitionIds
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task HandleAsync(PipelineDefinitionVersionsDeleting notification, CancellationToken cancellationToken)
    {
        await DeleteTriggersAsync(new TriggerFilter
        {
            PipelineDefinitionVersionIds = notification.Ids
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteTriggersAsync(TriggerFilter filter, CancellationToken cancellationToken)
    {
        await triggerIndexer.DeleteTriggersAsync(filter, cancellationToken).ConfigureAwait(false);
    }
}