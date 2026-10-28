using Stomme.Engine.Caching;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using Stomme.Engine.Pipelines.Runtime.Stores;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Handlers;

/// <summary>
/// A notification handler that invalidates triggers cache when pipeline definitions are refreshed.
/// </summary>
/// <remarks>
/// The class implements the <c>INotificationHandler</c> interface and is responsible for handling <c>PipelineDefinitionsRefreshed</c> notifications.
/// When a <c>PipelineDefinitionsRefreshed</c> notification is received, the <c>HandleAsync</c> method is called to invalidate the triggers cache.
/// The cache is invalidated by calling the <c>TriggerTokenAsync</c> method of the <c>ICacheManager</c> passed to the class constructor.
/// </remarks>
[UsedImplicitly]
public class InvalidateTriggersCache(ICacheManager cacheManager) :
    INotificationHandler<PipelineDefinitionsRefreshed>,
    INotificationHandler<PipelineDefinitionsReloaded>
{
    /// <inheritdoc />
    public Task HandleAsync(PipelineDefinitionsRefreshed notification, CancellationToken cancellationToken)
    {
        return cacheManager.TriggerTokenAsync(CachingTriggerStore.CacheInvalidationTokenKey, cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public async Task HandleAsync(PipelineDefinitionsReloaded notification, CancellationToken cancellationToken)
    {
        await cacheManager.TriggerTokenAsync(CachingTriggerStore.CacheInvalidationTokenKey, cancellationToken).ConfigureAwait(false);
    }
}