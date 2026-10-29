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
    // Lifted from the predecessor platform NotificationChannelQueries.cs. CHANGED: the predecessor platform gated these on the single
    // notifications:view key (split); a channel's configuration (endpoints, the credential's reference key,
    // transport options) is operator data, so reading it requires ManageChannels.

    /// <summary>Lists the current site's channels, by name.</summary>
    [Requires(NotificationsPermissions.ManageChannels)]
    public sealed record ListNotificationChannelsQuery : IQuery<IReadOnlyList<NotificationChannelSummary>>;

    public sealed class ListNotificationChannelsQueryHandler(INotificationChannelRepository channels, SiteState siteState)
        : IQueryHandler<ListNotificationChannelsQuery, IReadOnlyList<NotificationChannelSummary>>
    {
        public async Task<IReadOnlyList<NotificationChannelSummary>> Handle(ListNotificationChannelsQuery request, CancellationToken ct) =>
            (await channels.ListBySiteAsync(NotificationWire.RequireSiteId(siteState), ct))
                .OrderBy(channel => channel.Name, StringComparer.OrdinalIgnoreCase)
                .Select(NotificationWire.ToSummary)
                .ToList();
    }

    /// <summary>One channel's detail for the editor, or null when the site has no such channel. Never carries the secret.</summary>
    [Requires(NotificationsPermissions.ManageChannels)]
    public sealed record GetNotificationChannelQuery(Guid Id) : IQuery<NotificationChannelDetail?>;

    public sealed class GetNotificationChannelQueryHandler(INotificationChannelRepository channels, SiteState siteState)
        : IQueryHandler<GetNotificationChannelQuery, NotificationChannelDetail?>
    {
        public async Task<NotificationChannelDetail?> Handle(GetNotificationChannelQuery request, CancellationToken ct)
        {
            var channel = await channels.GetByIdAsync(request.Id, ct);
            return channel is null || channel.SiteId != NotificationWire.RequireSiteId(siteState) ? null : NotificationWire.ToDetail(channel);
        }
    }
}