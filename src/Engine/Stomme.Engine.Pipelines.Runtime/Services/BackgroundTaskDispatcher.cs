using Stomme.Engine.Mediator;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Notifications;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Relies on the <see cref="INotificationSender"/> to publish the received request as a domain event from a background worker.
/// </summary>
public class BackgroundTaskDispatcher(INotificationSender notificationSender) : ITaskDispatcher
{
    /// <inheritdoc />
    public async Task DispatchAsync(RunTaskRequest request, CancellationToken cancellationToken = default)
    {
        await notificationSender.SendAsync(request, NotificationStrategy.Background, cancellationToken).ConfigureAwait(false);
    }
}