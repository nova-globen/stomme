#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Mediator;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.CostOptimization.Application
{
    /// <summary>
    /// The weekly FinOps advisor (BL-97 increment 7, the predecessor platform's <c>CostOptimizationAdvisorJob</c>): one
    /// <see cref="RaiseRecommendationsCommand"/> pass per SITE of the job's tenant, each in its own scope under the site's
    /// alias so the read ports and the repository see that site. The handler is resolved directly rather than sent
    /// through the mediator - a system-initiated pass with no interactive user (the predecessor platform's own reasoning, the backup job's
    /// precedent). One site's failure is logged and contained so the remaining sites still get their pass.
    /// </summary>
    /// <remarks>
    /// <b>CHANGED from the predecessor platform:</b> per site (the recommendations and their signals are a site's) under the framework's job
    /// scheduler (weekly, a <see cref="HostedServiceBase"/> found by the assembly scan) instead of a cron over a tenant
    /// directory. A pass that finds nothing writes nothing, so a stock install - no cost ledger, no AI usage - is unchanged.
    /// </remarks>
    public class CostOptimizationAdvisorJob : HostedServiceBase
    {
        // JobType = "Stomme.Modules.CostOptimization.Application.CostOptimizationAdvisorJob, Stomme.Server"
        public CostOptimizationAdvisorJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Cost Optimization Advisor Job";
            Frequency = "w"; // week
            Interval = 1;
            IsEnabled = true;
        }

        public override async Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            var tenant = await provider.GetRequiredService<ITenantManager>().GetTenantAsync();
            var tenantId = tenant is null ? null : FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId);
            if (tenantId is null)
            {
                return "Cost Optimization Advisor: No Tenant Is Set For This Run<br />";
            }

            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var logger = provider.GetRequiredService<ILogger<CostOptimizationAdvisorJob>>();
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
                using var siteScope = scopes.CreateScope();
                try
                {
                    var tenantManager = siteScope.ServiceProvider.GetRequiredService<ITenantManager>();
                    tenantManager.SetTenant(tenant);
                    await tenantManager.SetAliasAsync(tenant!.TenantId, site.SiteId);
                    var run = await siteScope.ServiceProvider.GetRequiredService<IRequestHandler<RaiseRecommendationsCommand, AdvisorRunView>>()
                        .Handle(new RaiseRecommendationsCommand(), CancellationToken.None);
                    log.Append("Cost Optimization Advisor For Site: ").Append(site.Name)
                        .Append(" - Raised ").Append(run.RaisedCount).Append(", Suppressed ").Append(run.SuppressedCount).Append("<br />");
                }
                catch (Exception ex)
                {
                    // Contained per site: one site's failure must not cost the others their advisor pass.
                    logger.LogWarning(ex, "The FinOps advisor pass failed for site {SiteId} of tenant {TenantId}; continuing with the remaining sites.", site.SiteId, tenant!.TenantId);
                    log.Append("Cost Optimization Advisor Failed For Site: ").Append(site.Name).Append(" - ").Append(ex.GetType().Name).Append("<br />");
                }
            }

            return log.Length == 0 ? "Cost Optimization Advisor: No Sites<br />" : log.ToString();
        }
    }
}
