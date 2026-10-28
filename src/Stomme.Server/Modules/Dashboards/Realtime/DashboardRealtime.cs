#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.EventBus;
using Stomme.Extensions;
using Stomme.Modules.Datasets.Domain;
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

namespace Stomme.Modules.Dashboards.Realtime
{
    // ============================================================================================================
    // Real-time refresh (BL-96 increment 10, the predecessor platform's IDashboardDataSignal on Stomme's seams): a dataset's refresh or
    // schema change - a DOMAIN event of the Datasets module, dispatched in the request's scope - becomes ONE integration
    // event on the bus (delivered in-process when no bus is composed, the Pipelines pattern), whose handler pushes the
    // change to the widgets rendered on this host (the in-process signal, for an Interactive Server circuit) and to the
    // site's group on the Dashboards hub (for a browser). The hub is the opt-in SignalR transport (BL-53's declared
    // transport); with it off the push is a no-op and a browser page polls - its interval refresh - which is the default.
    // The message names the dataset and the kind of change, nothing else: the viewer re-reads through the API, under
    // its own key, so the hub never carries a row.
    // ============================================================================================================

    /// <summary>The integration event: which site's dataset changed and how.</summary>
    public sealed record DatasetChangedNotice(int TenantId, int SiteId, Guid DatasetId, string Kind);

    /// <summary>The transport-agnostic push port the change handler depends on; a no-op stands in when SignalR is off.</summary>
    public interface IDashboardRealtimePush
    {
        Task PushAsync(int tenantId, int siteId, DashboardDatasetChange change, CancellationToken cancellationToken = default);
    }

    public sealed class DashboardRealtimePush(IHubContext<DashboardHub> hub) : IDashboardRealtimePush
    {
        public Task PushAsync(int tenantId, int siteId, DashboardDatasetChange change, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(change);
            return hub.Clients.Group(DashboardHub.GroupFor(tenantId, siteId)).SendAsync(DashboardHubMethods.DatasetChanged, change, cancellationToken);
        }
    }

    public sealed class NoDashboardRealtimePush : IDashboardRealtimePush
    {
        public Task PushAsync(int tenantId, int siteId, DashboardDatasetChange change, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>The Dashboards hub: a connection joins its site's group; only a holder of <c>dashboards:view</c> connects (the hub policy).</summary>
    [Authorize]
    public sealed class DashboardHub : Hub
    {
        public static string GroupFor(int tenantId, int siteId) => "dashboards:" + tenantId + ":" + siteId;

        private static string GroupFor(string siteKey) => "dashboards:" + siteKey;

        public override async Task OnConnectedAsync()
        {
            var siteKey = Context.User?.SiteKey();
            if (!string.IsNullOrEmpty(siteKey))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, GroupFor(siteKey));
            }

            await base.OnConnectedAsync();
        }
    }

    /// <summary>Maps the hub when the SignalR transport is on - the module's own contribution, discovered by the transport scan.</summary>
    public sealed class DashboardEndpoints : IModuleEndpointContributor
    {
        public void MapEndpoints(IEndpointRouteBuilder endpoints, TransportOptions transports)
        {
            if (transports.SignalR.Enabled)
            {
                endpoints.MapHub<DashboardHub>(HubRoutes.Dashboards).RequireAuthorization(HubPolicies.Dashboards);
            }
        }
    }

    /// <summary>
    /// The Datasets domain events that mean a widget's data is stale, raised in the request's scope, carried as one
    /// integration event: on the bus when one is composed, delivered in-process otherwise.
    /// </summary>
    public sealed class DatasetChangeSignal(IServiceProvider services, SiteState siteState, ILogger<DatasetChangeSignal> logger)
        : IDomainEventHandler<DatasetRefreshedEvent>, IDomainEventHandler<DatasetSchemaChangedEvent>
    {
        public Task HandleAsync(DatasetRefreshedEvent domainEvent, CancellationToken ct = default) => SignalAsync(domainEvent.DatasetId, "Refreshed", ct);

        public Task HandleAsync(DatasetSchemaChangedEvent domainEvent, CancellationToken ct = default) => SignalAsync(domainEvent.DatasetId, "SchemaChanged", ct);

        private async Task SignalAsync(Guid datasetId, string kind, CancellationToken ct)
        {
            var alias = siteState.Alias;
            if (alias is null)
            {
                return; // a dataset changed outside a site's scope (a job): no viewer to tell
            }

            var notice = new DatasetChangedNotice(alias.TenantId, alias.SiteId, datasetId, kind);
            if (services.GetService<IEventBus>() is { } bus)
            {
                await bus.PublishAsync(notice, ct);
            }
            else
            {
                await DatasetChangedDelivery.DeliverAsync(services, notice, logger, ct);
            }
        }
    }

    /// <summary>The bus's handler: the change to the site's viewers, through the push port.</summary>
    public sealed class DatasetChangedNoticeHandler(IServiceProvider services, ILogger<DatasetChangedNoticeHandler> logger) : IIntegrationEventHandler<DatasetChangedNotice>
    {
        public Task HandleAsync(DatasetChangedNotice @event, CancellationToken ct = default) => DatasetChangedDelivery.DeliverAsync(services, @event, logger, ct);
    }

    internal static class DatasetChangedDelivery
    {
        public static async Task DeliverAsync(IServiceProvider services, DatasetChangedNotice notice, ILogger logger, CancellationToken ct)
        {
            var change = new DashboardDatasetChange(notice.DatasetId, notice.Kind);
            services.GetService<IDashboardDataSignal>()?.NotifyDatasetChanged(notice.TenantId + ":" + notice.SiteId, change);
            var push = services.GetService<IDashboardRealtimePush>();
            if (push is null)
            {
                return;
            }

            await push.PushAsync(notice.TenantId, notice.SiteId, change, ct);
            logger.LogDebug("Dataset {DatasetId} {Kind}: the dashboards of site {Site} of tenant {Tenant} were told.", notice.DatasetId, notice.Kind, notice.SiteId, notice.TenantId);
        }
    }
}
