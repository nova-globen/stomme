using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management.Notifications;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management.Handlers.Notifications;

/// <summary>
/// A pipeline definition notifications handler for evicting the cache of the pipeline definition service.
/// </summary>
/// <remarks>
/// This service listens for specific notifications and triggers cache invalidation operations accordingly.
/// </remarks>
[UsedImplicitly]
internal class EvictPipelineDefinitionServiceCache(IPipelineDefinitionCacheManager pipelineDefinitionCacheManager) :
    INotificationHandler<PipelineDefinitionDraftSaving>,
    INotificationHandler<PipelineDefinitionPublishing>,
    INotificationHandler<PipelineDefinitionRetracting>,
    INotificationHandler<PipelineDefinitionDeleting>,
    INotificationHandler<PipelineDefinitionsDeleting>,
    INotificationHandler<PipelineDefinitionVersionsUpdating>
{
    public Task HandleAsync(PipelineDefinitionDraftSaving notification, CancellationToken cancellationToken)
    {
        return pipelineDefinitionCacheManager.EvictPipelineDefinitionAsync(notification.PipelineDefinition.DefinitionId, cancellationToken);
    }
    
    /// <inheritdoc />
    public Task HandleAsync(PipelineDefinitionPublishing notification, CancellationToken cancellationToken)
    {
        return pipelineDefinitionCacheManager.EvictPipelineDefinitionAsync(notification.PipelineDefinition.DefinitionId, cancellationToken);
    }

    /// <inheritdoc />
    public Task HandleAsync(PipelineDefinitionRetracting notification, CancellationToken cancellationToken)
    {
        return pipelineDefinitionCacheManager.EvictPipelineDefinitionAsync(notification.PipelineDefinition.DefinitionId, cancellationToken);
    }

    /// <inheritdoc />
    public Task HandleAsync(PipelineDefinitionDeleting notification, CancellationToken cancellationToken)
    {
        return pipelineDefinitionCacheManager.EvictPipelineDefinitionAsync(notification.DefinitionId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task HandleAsync(PipelineDefinitionsDeleting notification, CancellationToken cancellationToken)
    {
        foreach (var definitionId in notification.DefinitionIds)
            await pipelineDefinitionCacheManager.EvictPipelineDefinitionAsync(definitionId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task HandleAsync(PipelineDefinitionVersionsUpdating notification, CancellationToken cancellationToken)
    {
        foreach (var definition in notification.PipelineDefinitions)
            await pipelineDefinitionCacheManager.EvictPipelineDefinitionAsync(definition.DefinitionId, cancellationToken).ConfigureAwait(false);
    }
}