using Stomme.Engine.Mediator.Contexts;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Mediator.Extensions;

namespace Stomme.Engine.Mediator.PublishingStrategies;

/// <summary>
/// Invokes event handlers in parallel and waits for the result.
/// </summary>
public class ParallelProcessingStrategy : IEventPublishingStrategy
{
    /// <inheritdoc />
    public async Task PublishAsync(NotificationStrategyContext context)
    {
        var notificationContext = context.NotificationContext;
        var notification = notificationContext.Notification;
        var notificationType = notification.GetType();
        var handleMethod = notificationType.GetNotificationHandlerMethod();
        var tasks = context.Handlers.Select(handler => handler.InvokeAsync(handleMethod, context.NotificationContext)).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}