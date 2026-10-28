using Stomme.Engine.Mediator.Contexts;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Mediator.Middleware.Notification.Contracts;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Mediator.Middleware.Notification.Components;

/// <inheritdoc />
[UsedImplicitly]
public class NotificationHandlerInvokerMiddleware(
    NotificationMiddlewareDelegate next,
    ILogger<NotificationHandlerInvokerMiddleware> logger)
    : INotificationMiddleware
{
    /// <inheritdoc />
    public async ValueTask InvokeAsync(NotificationContext context)
    {
        // Find all handlers for the specified notification.
        var notification = context.Notification;
        var notificationType = notification.GetType();
        var handlerType = typeof(INotificationHandler<>).MakeGenericType(notificationType);
        var serviceProvider = context.ServiceProvider;
        var notificationHandlers = serviceProvider.GetServices<INotificationHandler>();
        var handlers = notificationHandlers.Where(x => handlerType.IsInstanceOfType(x)).DistinctBy(x => x.GetType()).ToArray();
        var strategyContext = new NotificationStrategyContext(context, handlers, logger, serviceProvider, context.CancellationToken);

        await context.NotificationStrategy.PublishAsync(strategyContext).ConfigureAwait(false);

        // Invoke next middleware.
        await next(context).ConfigureAwait(false);
    }
}
