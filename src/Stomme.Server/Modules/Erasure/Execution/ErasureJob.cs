#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Modules.Erasure.Application;
using Stomme.Erasure.Persistence;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;
using Stomme.Infrastructure.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Erasure.Execution
{
    /// <summary>
    /// The erasure job: every minute, per tenant (the base runs each tenant in its own scope) and per
    /// site (its own DI scope with the site's alias set - for the cache as much as the path), under a lease per site
    /// so two nodes never run the same subject at once, it runs every open request through the participants. Immediate
    /// in the sense that a deletion is queued at once and erased on the next tick; no grace period.
    /// Always on - erasure is not a feature an install opts into.
    /// </summary>
    public sealed class ErasureJob : HostedServiceBase
    {
        public ErasureJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Subject Erasure";
            Frequency = "m";
            Interval = 1;
            IsEnabled = true;
        }

        public override async Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            var tenant = await provider.GetRequiredService<ITenantManager>().GetTenantAsync();
            var tenantId = tenant is null ? null : FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId);
            if (tenantId is null)
            {
                return "Erasure: No Tenant Is Set For This Run<br />";
            }

            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var logger = provider.GetRequiredService<ILogger<ErasureJob>>();
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
                await using var handle = locks is null ? null : await locks.AcquireAsync($"erasure:{tenant!.TenantId}:{site.SiteId}", TimeSpan.FromMinutes(10));
                if (locks is not null && handle is null)
                {
                    log.Append($"Erasure: Site {site.SiteId} Is Being Run Elsewhere<br />");
                    continue;
                }

                using var siteScope = scopes.CreateScope();
                var services = siteScope.ServiceProvider;
                var tenantManager = services.GetRequiredService<ITenantManager>();
                tenantManager.SetTenant(tenant);
                await tenantManager.SetAliasAsync(tenant!.TenantId, site.SiteId);

                var requests = services.GetRequiredService<IErasureRequestRepository>();
                var runner = services.GetRequiredService<ErasureRunner>();
                var open = await requests.ListOpenAsync(site.SiteId, DateTime.UtcNow);
                foreach (var request in open)
                {
                    try
                    {
                        var status = await runner.RunAsync(request);
                        log.Append($"Erasure {request.Id:N} On Site {site.SiteId}: {status}<br />");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Erasure {Request} on site {Site} could not be run.", request.Id, site.SiteId);
                        log.Append($"Erasure {request.Id:N} On Site {site.SiteId}: {ex.GetType().Name}<br />");
                    }
                }
            }

            return log.Length == 0 ? "Erasure: Nothing To Do<br />" : log.ToString();
        }
    }
}
