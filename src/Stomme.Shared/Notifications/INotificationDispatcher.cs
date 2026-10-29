using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models.Notifications;

namespace Stomme.Notifications
{
    /// <summary>
    /// The published producer port every module calls to "tell someone something".
    /// Dependency-free and in
    /// <c>Stomme.Shared</c>, so a producer depends on this and never on the Notifications module.
    /// </summary>
    /// <remarks>
    /// The implementation persists a <c>Queued</c> request WITH its parameters and returns; the per-minute dispatch
    /// job delivers it (the message bus, when registered, only shortens the wait). The port carries NO principal:
    /// producers are server code, so the implementation constrains RECIPIENTS rather than callers - an external
    /// e-mail address is accepted only when the site opts in, and <see cref="NotificationRecipientKind.Endpoint"/>
    /// is rejected at dispatch (F9: accepted unguarded it is an open relay and a path around the webhook SSRF guard).
    /// </remarks>
    public interface INotificationDispatcher
    {
        /// <summary>Queues a notification for delivery and returns the id of the created request (the send-log handle).</summary>
        Task<Guid> DispatchAsync(NotificationDispatchRequest request, CancellationToken ct = default);
    }
}