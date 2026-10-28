using Stomme.Engine.Mediator.Contexts;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Mediator.Extensions;

namespace Stomme.Engine.Mediator.PublishingStrategies;

/// <summary>
/// Invokes event handlers in sequence and waits for the result.
/// </summary>
public class SequentialProcessingStrategy : IEventPublishingStrategy
{
    /// <inheritdoc />
    public async Task PublishAsync(NotificationStrategyContext context)
    {
        var notificationContext = context.NotificationContext;
        var notification = notificationContext.Notification;
        var notificationType = notification.GetType();
        var handleMethod = notificationType.GetNotificationHandlerMethod();
        
        foreach (var handler in context.Handlers) 
            await handler.InvokeAsync(handleMethod, context.NotificationContext).ConfigureAwait(false);
    }
}