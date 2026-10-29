#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Models.Notifications;
using Stomme.Notifications;
using Stomme.Notifications.Persistence;
using Stomme.Security.Abstractions;
using Stomme.Shared;

namespace Stomme.Modules.Notifications.Application
{
    // Lifted from the predecessor platform NotificationRequestQueries.cs. SPLIT: the site's send log is
    // ViewLog; the caller's OWN list is ViewOwn and filters by ICurrentUser.UserId (int) AND the resolved site, so a
    // registered user sees only requests that address them on this site. The rendered subject/body are not persisted on a
    // request (they are rendered at delivery time), so the views carry the dispatch metadata and attempts.

    /// <summary>The current site's send log, most recent first.</summary>
    [Requires(NotificationsPermissions.ViewLog)]
    public sealed record ListNotificationRequestsQuery : IQuery<IReadOnlyList<NotificationRequestSummary>>;

    public sealed class ListNotificationRequestsQueryHandler(INotificationRequestRepository requests, SiteState siteState)
        : IQueryHandler<ListNotificationRequestsQuery, IReadOnlyList<NotificationRequestSummary>>
    {
        public async Task<IReadOnlyList<NotificationRequestSummary>> Handle(ListNotificationRequestsQuery request, CancellationToken ct) =>
            (await requests.ListBySiteAsync(NotificationWire.RequireSiteId(siteState), ct))
                .OrderByDescending(item => NotificationWire.LastAttemptOn(item) ?? item.CreatedOn)
                .Select(NotificationWire.ToSummary)
                .ToList();
    }

    /// <summary>One request's full delivery detail for the send log, or null when the site has no such request.</summary>
    [Requires(NotificationsPermissions.ViewLog)]
    public sealed record GetNotificationRequestQuery(Guid Id) : IQuery<NotificationRequestDetail?>;

    public sealed class GetNotificationRequestQueryHandler(INotificationRequestRepository requests, SiteState siteState)
        : IQueryHandler<GetNotificationRequestQuery, NotificationRequestDetail?>
    {
        public async Task<NotificationRequestDetail?> Handle(GetNotificationRequestQuery request, CancellationToken ct)
        {
            var item = await requests.GetByIdAsync(request.Id, ct);
            return item is null || item.SiteId != NotificationWire.RequireSiteId(siteState) ? null : NotificationWire.ToDetail(item);
        }
    }

    /// <summary>
    /// The caller's OWN recent notifications on the current site: requests with a User recipient whose id is the caller's,
    /// most recent first. Anonymous (no user id) is an empty list - though <c>[Requires(ViewOwn)]</c> denies it first.
    /// </summary>
    /// <remarks>
    /// The recipient list is a JSON collection column, which <c>FindAsync</c> cannot filter, so the site's requests are read
    /// (a non-string scalar filter) and matched in memory. Bounded by the retention purge; an index is a residue.
    /// </remarks>
    [Requires(NotificationsPermissions.ViewOwn)]
    public sealed record ListMyNotificationsQuery : IQuery<IReadOnlyList<NotificationRequestSummary>>;

    public sealed class ListMyNotificationsQueryHandler(INotificationRequestRepository requests, ICurrentUser currentUser, SiteState siteState)
        : IQueryHandler<ListMyNotificationsQuery, IReadOnlyList<NotificationRequestSummary>>
    {
        public async Task<IReadOnlyList<NotificationRequestSummary>> Handle(ListMyNotificationsQuery request, CancellationToken ct)
        {
            if (currentUser.UserId is not { } userId || userId <= 0)
            {
                return Array.Empty<NotificationRequestSummary>();
            }

            return (await requests.ListBySiteAsync(NotificationWire.RequireSiteId(siteState), ct))
                .Where(item => NotificationWire.Targets(item, userId))
                .OrderByDescending(item => NotificationWire.LastAttemptOn(item) ?? item.CreatedOn)
                .Select(NotificationWire.ToSummary)
                .ToList();
        }
    }
}