#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Portability.Execution
{
    /// <summary>
    /// Builds the queued portability packages, every minute, per tenant and site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A Building row IS the queue.</b> The spec queued a <c>SiteTask</c> per build; it is not used, because
    /// <c>SiteTaskController</c> accepts any task body from any Administrator and <c>SiteTaskJob</c> instantiates whatever
    /// type it names - a module-owned job draining its own rows has none of that surface, and it is
    /// the pattern <c>ExportQueueJob</c> already proves. Nothing a request supplies names a type to run.
    /// </para>
    /// <para>
    /// <b>The scope discipline is <c>ExportQueueJob</c>'s</b>: <c>BeginScope</c> for the tenant, then a
    /// fresh DI scope per site with <c>SetTenant</c> + <c>SetAliasAsync</c>, because the folder path comes from the
    /// resolved TENANT and user-scoped caches from the alias.
    /// </para>
    /// <para>
    /// <b>One builder per site at a time, across nodes:</b> the install's <see cref="IDistributedLock"/> is taken
    /// per site; a node that cannot take it leaves that site to the one that did.
    /// </para>
    /// </remarks>
    public sealed class PortabilityBuildJob : HostedServiceBase
    {
        public PortabilityBuildJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Portability Build Job";
            Frequency = "m";
            Interval = 1;
            IsEnabled = true; // gated at run time by Portability:Enabled
        }

        public override async Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            if (!provider.GetRequiredService<PortabilityOptions>().Enabled)
            {
                return "Portability Is Disabled (" + PortabilityOptions.SectionName + ":Enabled)<br />";
            }

            var tenant = await provider.GetRequiredService<ITenantManager>().GetTenantAsync();
            var tenantId = tenant is null ? null : FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId);
            if (tenantId is null)
            {
                return "Portability: No Tenant Is Set For This Run<br />";
            }

            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var logger = provider.GetRequiredService<ILogger<PortabilityBuildJob>>();
            var locks = provider.GetService<IDistributedLock>();

            using var tenantScope = provider.GetRequiredService<ITenantScopeFactory>().BeginScope(tenantId.Value);

            List<Site> sites;
            using (var listing = scopes.CreateScope())
            {
                listing.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                sites = (await listing.ServiceProvider.GetRequiredService<ISiteRepository>().GetSitesAsync()).Where(site => !site.IsDeleted).ToList();
            }

            var log = new StringBuilder();
            foreach (var site in sites)
            {
                await using var handle = locks is null ? null : await locks.AcquireAsync($"portability:{tenant!.TenantId}:{site.SiteId}", TimeSpan.FromMinutes(10));
                if (locks is not null && handle is null)
                {
                    // Another run holds this site - another node, or this node's own scheduled tick. Said in the notes: a
                    // silent skip reads exactly like "nothing queued", which cost a diagnosis once already.
                    log.Append("Portability For Site: ").Append(site.Name).Append(" - Skipped, Another Run Holds It<br />");
                    continue;
                }

                using var siteScope = scopes.CreateScope();
                try
                {
                    var tenantManager = siteScope.ServiceProvider.GetRequiredService<ITenantManager>();
                    tenantManager.SetTenant(tenant);
                    await tenantManager.SetAliasAsync(tenant!.TenantId, site.SiteId);

                    var (built, failed) = await siteScope.ServiceProvider.GetRequiredService<PortabilityBuilder>().DrainSiteAsync(site.SiteId);
                    if (built + failed > 0)
                    {
                        log.Append("Portability For Site: ").Append(site.Name).Append($" - {built} Built, {failed} Failed<br />");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "The portability build failed for site {SiteName} ({SiteId}) of tenant {TenantId}.", site.Name, site.SiteId, tenant!.TenantId);
                    log.Append("Portability Failed For Site: ").Append(site.Name).Append(" - ").Append(ex.GetType().Name).Append("<br />");
                }
            }

            return log.Length == 0 ? "Portability: Nothing Queued<br />" : log.ToString();
        }
    }
}
