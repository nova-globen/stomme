#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.EventBus;
using Stomme.Extensions;
using Stomme.Modules.Collaboration.Application;
using Stomme.Modules.Collaboration.Domain;
using Stomme.Modules.Dashboards.Domain;
using Stomme.Security.Abstractions;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Transport;
using Stomme.Transport.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Collaboration.Realtime
{
    // ============================================================================================================
    // Presence and the change feed - the Dashboards hub's shape. A lock taken
    // or released, a presence change, an announced change - the collaboration aggregates' DOMAIN events, dispatched once
    // their unit of work is saved - and a resource stored by the module that owns it (a pipeline's graph, a board's layout)
    // become ONE integration event on the bus (delivered in-process when no bus is composed), whose handler tells the editors
    // rendered on this host (the in-process signal, for an Interactive Server circuit) and the site's group on the
    // collaboration hub (for a browser). The hub is the opt-in SignalR transport; with it off the push is a no-op and the
    // editor polls. The notice names the resource, the kind of change and who caused it - nothing else: an editor re-reads
    // the session and the lock through the API, under its own keys and the owning module's access check.
    // CHANGED from the predecessor platform, whose hub carried presence positions, change descriptors and lock expiries to a per-resource
    // group and whose hub methods drove the services: here the hub only carries notices, and every act is the REST API's -
    // one surface, one authorisation, and a hub invocation never needs a site's tenant scope. the predecessor platform's store change feed
    // and polling notifier (a session saved on another node) are the bus: every node's handler pushes to its own clients.
    // ============================================================================================================

    /// <summary>The integration event: which site's resource, what kind of change, and who caused it.</summary>
    public sealed record CollaborationChangedNotice(int TenantId, int SiteId, string ResourceType, string ResourceId, string Kind, int ByUserId);

    /// <summary>The transport-agnostic push port; a no-op stands in when SignalR is off.</summary>
    public interface ICollaborationRealtimePush
    {
        Task PushAsync(int tenantId, int siteId, CollaborationNotice notice, CancellationToken cancellationToken = default);
    }

    public sealed class CollaborationRealtimePush(IHubContext<CollaborationHub> hub) : ICollaborationRealtimePush
    {
        public Task PushAsync(int tenantId, int siteId, CollaborationNotice notice, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(notice);
            return hub.Clients.Group(CollaborationHub.GroupFor(tenantId, siteId)).SendAsync(CollaborationHubMethods.Changed, notice, cancellationToken);
        }
    }

    public sealed class NoCollaborationRealtimePush : ICollaborationRealtimePush
    {
        public Task PushAsync(int tenantId, int siteId, CollaborationNotice notice, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>The collaboration hub: a connection joins its site's group; only a holder of <c>collaboration:view</c> connects (the hub policy).</summary>
    [Authorize]
    public sealed class CollaborationHub : Hub
    {
        public static string GroupFor(int tenantId, int siteId) => "collaboration:" + tenantId + ":" + siteId;

        public override async Task OnConnectedAsync()
        {
            var siteKey = Context.User?.SiteKey();
            if (!string.IsNullOrEmpty(siteKey))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, "collaboration:" + siteKey);
            }

            await base.OnConnectedAsync();
        }
    }

    /// <summary>Maps the hub when the SignalR transport is on - the module's own contribution, discovered by the transport scan.</summary>
    public sealed class CollaborationEndpoints : IModuleEndpointContributor
    {
        public void MapEndpoints(IEndpointRouteBuilder endpoints, TransportOptions transports)
        {
            if (transports.SignalR.Enabled)
            {
                endpoints.MapHub<CollaborationHub>(HubRoutes.Collaboration).RequireAuthorization(HubPolicies.Collaboration);
            }
        }
    }

    /// <summary>
    /// Tells a resource's editors that something happened to it - the port the owning modules' saves call and the domain
    /// signal below uses. Silent while <c>Collaboration:Enabled</c> is off (editing is then exactly the single-editor
    /// behaviour) and outside a site's scope. A failure to tell is logged, never the caller's failure: a missed notice is
    /// staleness, and the editor's poll or the next notice catches it up.
    /// </summary>
    public interface ICollaborationNotifier
    {
        Task NotifyAsync(string resourceType, string resourceId, string kind, int byUserId, CancellationToken cancellationToken = default);
    }

    // SiteState is optional: a narrower composition (a store's conformance harness) saves these aggregates with no site at all.
    public sealed class CollaborationNotifier(IServiceProvider services, ILogger<CollaborationNotifier> logger, SiteState? siteState = null) : ICollaborationNotifier
    {
        public async Task NotifyAsync(string resourceType, string resourceId, string kind, int byUserId, CancellationToken cancellationToken = default)
        {
            // Resolved lazily: a host composed without the collaboration module's startup has no options, which is off.
            if (services.GetService<CollaborationOptions>() is not { Enabled: true } || siteState?.Alias is not { } alias)
            {
                return;
            }

            var notice = new CollaborationChangedNotice(alias.TenantId, alias.SiteId, resourceType, resourceId, kind, byUserId);
            try
            {
                if (services.GetService<IEventBus>() is { } bus)
                {
                    await bus.PublishAsync(notice, cancellationToken);
                }
                else
                {
                    await CollaborationChangedDelivery.DeliverAsync(services, notice, logger, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The collaboration notice ({Kind}) for {ResourceType}/{ResourceId} was not delivered; its editors catch up on their next poll or notice.", kind, resourceType, resourceId);
            }
        }
    }

    /// <summary>
    /// The domain events that mean a resource's editors should look again: the collaboration aggregates' own, and a board
    /// stored by the Dashboards module. Who caused it is the request's user where there is one (an administrator's take-over,
    /// an editor's save), the event's own user otherwise (a lapsed lease expired by the sweep).
    /// </summary>
    public sealed class CollaborationDomainSignal(ICollaborationNotifier notifier, ICurrentUser? currentUser = null)
        : IDomainEventHandler<LockAcquiredEvent>, IDomainEventHandler<LockReleasedEvent>, IDomainEventHandler<PresenceChangedEvent>, IDomainEventHandler<ChangeBroadcastEvent>,
          IDomainEventHandler<DashboardUpdatedEvent>, IDomainEventHandler<WidgetAddedEvent>, IDomainEventHandler<DashboardPublishedEvent>
    {
        public Task HandleAsync(LockAcquiredEvent domainEvent, CancellationToken ct = default) => ForAsync(domainEvent.Resource, CollaborationNoticeKinds.Lock, domainEvent.HolderUserId, ct);

        public Task HandleAsync(LockReleasedEvent domainEvent, CancellationToken ct = default) => ForAsync(domainEvent.Resource, CollaborationNoticeKinds.Lock, domainEvent.HolderUserId, ct);

        public Task HandleAsync(PresenceChangedEvent domainEvent, CancellationToken ct = default) => ForAsync(domainEvent.Resource, CollaborationNoticeKinds.Presence, domainEvent.UserId, ct);

        public Task HandleAsync(ChangeBroadcastEvent domainEvent, CancellationToken ct = default) => ForAsync(domainEvent.Resource, CollaborationNoticeKinds.Change, domainEvent.AuthorUserId, ct);

        public Task HandleAsync(DashboardUpdatedEvent domainEvent, CancellationToken ct = default) => SavedAsync(domainEvent.DashboardId, ct);

        public Task HandleAsync(WidgetAddedEvent domainEvent, CancellationToken ct = default) => SavedAsync(domainEvent.DashboardId, ct);

        public Task HandleAsync(DashboardPublishedEvent domainEvent, CancellationToken ct = default) => SavedAsync(domainEvent.DashboardId, ct);

        private Task SavedAsync(Guid dashboardId, CancellationToken ct) =>
            notifier.NotifyAsync(CollaborativeResourceTypes.Dashboard, dashboardId.ToString("D"), CollaborationNoticeKinds.Saved, currentUser?.UserId ?? 0, ct);

        private Task ForAsync(string resource, string kind, int eventUserId, CancellationToken ct)
        {
            var reference = ResourceRef.Create(resource);
            return notifier.NotifyAsync(reference.ResourceType, reference.ResourceId, kind, currentUser?.UserId is int userId && userId > 0 ? userId : eventUserId, ct);
        }
    }

    /// <summary>The bus's handler: the notice to the site's editors, through the signal and the push port.</summary>
    public sealed class CollaborationChangedNoticeHandler(IServiceProvider services, ILogger<CollaborationChangedNoticeHandler> logger) : IIntegrationEventHandler<CollaborationChangedNotice>
    {
        public Task HandleAsync(CollaborationChangedNotice @event, CancellationToken ct = default) => CollaborationChangedDelivery.DeliverAsync(services, @event, logger, ct);
    }

    internal static class CollaborationChangedDelivery
    {
        public static async Task DeliverAsync(IServiceProvider services, CollaborationChangedNotice notice, ILogger logger, CancellationToken ct)
        {
            var message = new CollaborationNotice(notice.ResourceType, notice.ResourceId, notice.Kind, notice.ByUserId);
            services.GetService<ICollaborationSignal>()?.Notify(notice.TenantId + ":" + notice.SiteId, message);
            if (services.GetService<ICollaborationRealtimePush>() is { } push)
            {
                await push.PushAsync(notice.TenantId, notice.SiteId, message, ct);
                logger.LogDebug("{Kind} on {ResourceType}/{ResourceId}: the editors of site {Site} of tenant {Tenant} were told.", notice.Kind, notice.ResourceType, notice.ResourceId, notice.SiteId, notice.TenantId);
            }
        }
    }
}
