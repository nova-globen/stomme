using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Stomme.FeatureManagement;
using Stomme.FeatureManagement.Application;
using Stomme.Infrastructure.Tenancy;
using Stomme.Modules.Notifications.Application;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// The authoritative delivery loop of the Notifications module: every minute, per tenant,
    /// per non-deleted site, <see cref="NotificationDispatchProcessor.RunSiteAsync"/> - due requests delivered with scalar
    /// retries, quiet-hours deferral and digest holds, plus the hourly digest flush and retention purge. ENABLED by default
    /// (a producer's dispatch must reach someone on a stock install); gated at run time by the <c>notifications.dispatch</c>
    /// feature flag. The message bus, when enabled, only shortens the wait.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Scopes (the trap this job is built around).</b>
    /// <c>HostedServiceBase</c> gives each tenant of a run its own ambient tenant scope and its own DI scope. The job still does no module work
    /// in the scope it is HANDED, so that it is correct whatever its caller's scope is rather than only under a
    /// well-behaved base class. The hazard is unchanged: the seam's <c>StommeTenantContext</c> caches the first alias's
    /// tenant per DI scope, so a module-store read in a mis-scoped scope is silently filtered as the WRONG tenant, and one
    /// change tracker would span tenants. It reads the tenant the base set, enters that tenant's AMBIENT scope
    /// (<see cref="ITenantScopeFactory.BeginScope"/>) and only THEN creates fresh DI scopes - one to list the sites, one per
    /// site with the site's alias set - the <c>FeatureSeeder</c> order. A site's failure is logged and contained.
    /// </para>
    /// <para>
    /// No cancellation token reaches <c>ExecuteJobAsync</c>: the processor bounds each render itself.
    /// </para>
    /// </remarks>
    public class NotificationDispatchJob : HostedServiceBase
    {
        // JobType = "Stomme.Infrastructure.NotificationDispatchJob, Stomme.Server"

        public NotificationDispatchJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Notification Dispatch Job";
            Frequency = "m"; // minute
            Interval = 1;
            IsEnabled = true;
        }

        public override async Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            var log = new StringBuilder();
            var tenant = await provider.GetRequiredService<ITenantManager>().GetTenantAsync();
            var tenantId = tenant is null ? null : FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId);
            if (tenantId is null)
            {
                return "Notification Dispatch: No Tenant Is Set For This Run<br />";
            }

            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var logger = provider.GetRequiredService<ILogger<NotificationDispatchJob>>();
            using var tenantScope = provider.GetRequiredService<ITenantScopeFactory>().BeginScope(tenantId.Value);

            System.Collections.Generic.List<Models.Site> sites;
            using (var listing = scopes.CreateScope())
            {
                listing.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);

                // Read once per run (per tenant), as NotificationJob reads notifications.email.
                if (!await listing.ServiceProvider.GetRequiredService<IFeatureEvaluator>().IsEnabledAsync(FeatureCatalog.NotificationDispatch))
                {
                    return "Notification Dispatch Is Disabled By The " + FeatureCatalog.NotificationDispatch + " Feature Flag<br />";
                }

                sites = (await listing.ServiceProvider.GetRequiredService<ISiteRepository>().GetSitesAsync()).Where(site => !site.IsDeleted).ToList();
            }

            foreach (var site in sites)
            {
                using var siteScope = scopes.CreateScope();
                try
                {
                    var tenantManager = siteScope.ServiceProvider.GetRequiredService<ITenantManager>();
                    tenantManager.SetTenant(tenant);
                    await tenantManager.SetAliasAsync(tenant.TenantId, site.SiteId);
                    if (siteScope.ServiceProvider.GetRequiredService<Shared.SiteState>().Alias is null)
                    {
                        tenantManager.SetTenant(tenant); // a site with no alias: keep the tenant routing (SetAliasAsync cleared it)
                    }

                    var result = await siteScope.ServiceProvider.GetRequiredService<NotificationDispatchProcessor>().RunSiteAsync(site.SiteId);
                    log.Append("Notification Dispatch For Site: ").Append(site.Name)
                        .Append($" - Delivered {result.Delivered}, Retried {result.Retried}, Failed {result.Failed}, Deferred {result.Deferred}, Held For Digest {result.DigestHeld}")
                        .Append(result.HourlyPassRan ? $", Flushed {result.Flushed}, Purged {result.Purged}" : string.Empty)
                        .Append("<br />");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Notification dispatch failed for site {SiteName} ({SiteId}) of tenant {TenantId}.", site.Name, site.SiteId, tenant.TenantId);
                    log.Append("Notification Dispatch Failed For Site: ").Append(site.Name).Append(" - ").Append(ex.GetType().Name).Append("<br />");
                }
            }

            return log.ToString();
        }
    }
}
