using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Common.Multitenancy;

public class TenantEventsManager(
    IEnumerable<ITenantActivatedEvent> tenantActivatedEvents, 
    IEnumerable<ITenantDeactivatedEvent> tenantDeactivatedEvents,
    IEnumerable<ITenantDeletedEvent> tenantDeletedEvents,
    ILogger<TenantEventsManager> logger)
{
    public async Task TenantActivatedAsync(TenantActivatedEventArgs args)
    {
        await ExecuteEventHandlersAsync(
            tenantActivatedEvents,
            (handler, eventArgs) => handler.TenantActivatedAsync(eventArgs),
            args,
            "activated").ConfigureAwait(false);
    }

    public async Task TenantDeactivatedAsync(TenantDeactivatedEventArgs args)
    {
        await ExecuteEventHandlersAsync(
            tenantDeactivatedEvents,
            (handler, eventArgs) => handler.TenantDeactivatedAsync(eventArgs),
            args,
            "deactivated").ConfigureAwait(false);
    }
    
    public async Task TenantDeletedAsync(TenantDeletedEventArgs args)
    {
        await ExecuteEventHandlersAsync(
            tenantDeletedEvents,
            (handler, eventArgs) => handler.TenantDeletedAsync(eventArgs),
            args,
            "deleted").ConfigureAwait(false);
    }

    private async Task ExecuteEventHandlersAsync<THandler, TArgs>(
        IEnumerable<THandler> handlers,
        Func<THandler, TArgs, Task> handlerAction,
        TArgs args,
        string eventType)
    {
        foreach (var handler in handlers)
        {
            try
            {
                await handlerAction(handler, args).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error occurred while processing tenant {EventType} event.", eventType);
            }
        }
    }
}