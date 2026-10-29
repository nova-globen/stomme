using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;

namespace Stomme.Transport.Realtime
{
    /// <summary>
    /// A realtime message pushed to a user's connected clients over <see cref="NotificationHub"/>.
    /// </summary>
    public sealed record NotificationRealtimeMessage(string UserId, string Subject, string Body);

    /// <summary>
    /// The transport-agnostic port a producing feature (e.g. a notification service) depends on to push an
    /// in-app notification in realtime, without referencing SignalR. The host registers
    /// <see cref="NotificationRealtimePush"/> as the adapter when the SignalR transport is enabled; when it is
    /// not, the port is simply absent and producers fall back to their non-realtime path.
    /// </summary>
    public interface INotificationRealtimePush
    {
        Task PushAsync(NotificationRealtimeMessage message, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Host adapter for <see cref="INotificationRealtimePush"/>: pushes a rendered in-app notification to the
    /// target user's connected clients over <see cref="NotificationHub"/>. SignalR's per-user grouping
    /// (<c>Clients.User</c>) resolves every connection authenticated as that user id, so a notification reaches
    /// all of a user's open sessions. This is the reference push adapter (brief 04 Phase 2).
    /// </summary>
    public sealed class NotificationRealtimePush : INotificationRealtimePush
    {
        private readonly IHubContext<NotificationHub> _hub;

        public NotificationRealtimePush(IHubContext<NotificationHub> hub)
        {
            _hub = hub;
        }

        public Task PushAsync(NotificationRealtimeMessage message, CancellationToken cancellationToken = default)
        {
            System.ArgumentNullException.ThrowIfNull(message);
            return _hub.Clients.User(message.UserId)
                .SendAsync(NotificationHub.NotificationMethod, message, cancellationToken);
        }
    }
}
