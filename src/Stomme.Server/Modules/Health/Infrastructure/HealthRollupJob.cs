#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Modules.Health.Application;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Health.Infrastructure
{
    /// <summary>
    /// Folds the Minute series into Hours and Days once an hour, and sweeps stranded Minute rows.
    /// </summary>
    /// <remarks>
    /// Hourly rather than per minute because it recomputes whole windows: doing that every minute would rewrite
    /// the same Hour row sixty times to reach the same answer. The pass is idempotent, so a missed run costs
    /// nothing but freshness, and a double run costs nothing at all.
    /// </remarks>
    public class HealthRollupJob : HostedServiceBase
    {
        // JobType = "Stomme.Modules.Health.Infrastructure.HealthRollupJob, Stomme.Server"

        public HealthRollupJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Health Rollup Job";
            Frequency = "H"; // hour
            Interval = 1;
            IsEnabled = true; // gated at run time by Health:Enabled
        }

        public override async Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            if (!HealthSettings.ProbingEnabled(HostConfiguration.Resolve(provider)))
            {
                return "Health Probing Is Disabled (Health:Enabled)<br />";
            }

            var tenant = await provider.GetRequiredService<ITenantManager>().GetTenantAsync();
            var tenantId = tenant is null ? null : FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId);
            if (tenantId is null)
            {
                return "Health Rollup: No Tenant Is Set For This Run<br />";
            }

            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var logger = provider.GetRequiredService<ILogger<HealthRollupJob>>();

            using var tenantScope = provider.GetRequiredService<ITenantScopeFactory>().BeginScope(tenantId.Value);

            List<Site> sites;
            using (var listing = scopes.CreateScope())
            {
                listing.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);
                sites = (await listing.ServiceProvider.GetRequiredService<ISiteRepository>().GetSitesAsync())
                    .Where(site => !site.IsDeleted).ToList();
            }

            var log = new StringBuilder();

            // The same shape the probe job uses: the wider scopes once per tenant against site 0, then each site.
            var work = new List<(HealthProbeScope Scope, int SiteId, string Label)>
            {
                (HealthProbeScope.Installation, 0, "Installation"),
                (HealthProbeScope.Tenant, 0, "Tenant")
            };
            work.AddRange(sites.Select(site => (HealthProbeScope.Site, site.SiteId, "Site " + site.Name)));

            foreach (var (scope, siteId, label) in work)
            {
                using var siteScope = scopes.CreateScope();
                try
                {
                    var tenantManager = siteScope.ServiceProvider.GetRequiredService<ITenantManager>();
                    tenantManager.SetTenant(tenant);

                    if (siteId > 0)
                    {
                        await tenantManager.SetAliasAsync(tenant!.TenantId, siteId);
                        if (siteScope.ServiceProvider.GetRequiredService<Shared.SiteState>().Alias is null)
                        {
                            tenantManager.SetTenant(tenant);
                        }
                    }

                    var result = await siteScope.ServiceProvider.GetRequiredService<HealthRollupPass>()
                        .RunAsync(tenantId, scope, siteId).ConfigureAwait(false);

                    if (result.HoursWritten + result.DaysWritten + result.MinutesSwept > 0)
                    {
                        log.Append("Health Rollup ").Append(label).Append(": ")
                            .Append(result.HoursWritten).Append(" Hour(s), ")
                            .Append(result.DaysWritten).Append(" Day(s), ")
                            .Append(result.MinutesSwept).Append(" Minute Row(s) Swept<br />");
                    }
                }
                catch (Exception ex)
                {
                    // Contained per scope and per site, like the probe job: one failure must not cost the rest.
                    logger.LogError(ex, "The health rollup failed for {Scope} {Label} of tenant {TenantId}.", scope, label, tenant!.TenantId);
                    log.Append("Health Rollup Failed For ").Append(label).Append(" - ").Append(ex.GetType().Name).Append("<br />");
                }
            }

            return log.Length == 0 ? "Health Rollup: Nothing To Roll Up<br />" : log.ToString();
        }
    }
}
