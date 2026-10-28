using Stomme.Engine.Mediator.Contexts;
using Stomme.Engine.Mediator.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Mediator.PublishingStrategies;

/// <summary>
/// Invokes event handlers in parallel and does not wait for the result.
/// </summary>
public class BackgroundProcessingStrategy : IEventPublishingStrategy
{
    /// <inheritdoc />
    public async Task PublishAsync(NotificationStrategyContext context)
    {
        var notificationsChannel = context.ServiceProvider.GetRequiredService<INotificationsChannel>();

        await notificationsChannel.Writer.WriteAsync(context.NotificationContext, context.CancellationToken).ConfigureAwait(false);
    }
}