using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Handlers;

/// <summary>
/// A notification handler that invalidates the pipeline cache when pipeline definitions are reloaded.
/// </summary>
/// <remarks>
/// The class implements the <c>INotificationHandler</c> interface and is responsible for handling <c>PipelineDefinitionsReloaded</c> notifications.
/// When a <c>PipelineDefinitionsReloaded</c> notification is received, the <c>HandleAsync</c> method is called to invalidate the http definition cache.
/// </remarks>
[UsedImplicitly]
public class InvalidatePipelinesCache(IPipelineDefinitionCacheManager pipelineDefinitionCacheManager) : INotificationHandler<PipelineDefinitionsReloaded>
{
    /// <inheritdoc />
    public async Task HandleAsync(PipelineDefinitionsReloaded notification, CancellationToken cancellationToken)
    {
        foreach (var reloadedPipelineDefinition in notification.ReloadedPipelineDefinitions)
            await pipelineDefinitionCacheManager.EvictPipelineDefinitionAsync(reloadedPipelineDefinition.DefinitionId, cancellationToken).ConfigureAwait(false);
    }
}