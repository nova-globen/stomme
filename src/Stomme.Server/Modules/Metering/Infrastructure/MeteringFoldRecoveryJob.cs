#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Models;
using Stomme.Modules.Metering.Application;
using Stomme.Modules.Metering.Domain;
using Stomme.Metering.Persistence;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Metering.Infrastructure
{
    /// <summary>
    /// Folds ledger rows that were appended but never counted (D102 item 4d).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This job exists because the append and the fold are two commits.</b> Stomme's unit of work persists
    /// nothing of its own - every repository call commits eagerly - so the predecessor platform's single-<c>SaveChanges</c>
    /// atomicity does not survive the lift. Rather than claim an atomicity the seam cannot provide, the recorder
    /// writes the ledger row <c>Pending</c>, folds, and flips it to <c>Applied</c>; a crash or a lock timeout in
    /// between leaves a Pending row, and this job is what makes that recoverable instead of a silent under-count.
    /// </para>
    /// <para>
    /// <b>The grace period is not cosmetic.</b> A row younger than <see cref="GraceMinutes"/> may simply be mid-fold
    /// in another request, and folding it here would double-count it. Only rows older than the grace period - long
    /// past any live fold - are taken.
    /// </para>
    /// <para>
    /// <b>Scopes - and a correction, 2026-09-20.</b> This paragraph used to say <c>HostedServiceBase</c> hands ONE
    /// DI scope to every tenant of a run. It does not, and had not since <c>ff19714</c>: the
    /// base class enters an ambient tenant scope and a FRESH DI scope per tenant, with a per-tenant catch. The
    /// behaviour below is kept anyway, for a narrower reason - this job does no module work in the scope it is
    /// HANDED, so it is correct whatever its caller's scope is, not only when its caller is the fixed base class.
    /// The hazard that makes that worth doing is unchanged: the seam's tenant context caches the FIRST alias's
    /// tenant per DI scope, so a module-store read in a mis-scoped scope is silently filtered as the WRONG tenant
    /// instead of failing. It therefore enters the tenant's AMBIENT scope and only then creates fresh DI scopes.
    /// </para>
    /// </remarks>
    public class MeteringFoldRecoveryJob : HostedServiceBase
    {
        // JobType = "Stomme.Modules.Metering.Infrastructure.MeteringFoldRecoveryJob, Stomme.Server"

        /// <summary>How long a Pending row is left alone before it is treated as abandoned.</summary>
        internal const int GraceMinutes = 5;

        public MeteringFoldRecoveryJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Metering Fold Recovery Job";
            Frequency = "m"; // minute
            Interval = 5;
            IsEnabled = true; // gated at run time by Metering:Enabled - see MeteringSwitch
        }

        public override async Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            if (!MeteringSwitch.IsEnabled(HostConfiguration.Resolve(provider)))
            {
                return "Metering Is Disabled (" + MeteringSwitch.EnabledKey + ")<br />";
            }

            var tenant = await provider.GetRequiredService<ITenantManager>().GetTenantAsync();
            var tenantId = tenant is null ? null : FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId);
            if (tenantId is null)
            {
                return "Metering Fold Recovery: No Tenant Is Set For This Run<br />";
            }

            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var logger = provider.GetRequiredService<ILogger<MeteringFoldRecoveryJob>>();
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
                try
                {
                    var folded = await RecoverSiteAsync(scopes, tenant!, site, tenantId);
                    if (folded > 0)
                    {
                        log.Append("Metering Fold Recovery For Site: ").Append(site.Name).Append($" - Folded {folded} Pending Row(s)<br />");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Metering fold recovery failed for site {SiteName} ({SiteId}) of tenant {TenantId}.", site.Name, site.SiteId, tenant!.TenantId);
                    log.Append("Metering Fold Recovery Failed For Site: ").Append(site.Name).Append(" - ").Append(ex.GetType().Name).Append("<br />");
                }
            }

            return log.Length == 0 ? "Metering Fold Recovery: Nothing Pending<br />" : log.ToString();
        }

        private static async Task<int> RecoverSiteAsync(IServiceScopeFactory scopes, Tenant tenant, Site site, Tenancy.Domain.TenantId tenantId)
        {
            List<UsageRecordAggregate> pending;
            var cutoff = DateTime.UtcNow.AddMinutes(-GraceMinutes);

            using (var siteScope = scopes.CreateScope())
            {
                await RouteAsync(siteScope.ServiceProvider, tenant, site);
                pending = (await siteScope.ServiceProvider.GetRequiredService<IUsageRecordRepository>()
                    .ListPendingAsync(site.SiteId, cutoff)).ToList();
            }

            if (pending.Count == 0)
            {
                return 0;
            }

            var folded = 0;
            foreach (var row in pending)
            {
                // The recorder owns the fold, including the lock, the fresh-scope retry and the Applied flip; the
                // job supplies the row's OWN quantity and instant so a recovered fold counts exactly what the
                // append recorded - never "now", which would land in the wrong calendar window.
                using var recoverScope = scopes.CreateScope();
                await RouteAsync(recoverScope.ServiceProvider, tenant, site);
                var recorder = recoverScope.ServiceProvider.GetRequiredService<UsageRecorder>();
                // MeteringTime.AsUtc: the row was read back from the store with no kind, and a conversion here
                // would move a recovered fold into the previous window.
                await recorder.FoldAsync(site.SiteId, tenantId, row.Metric, row.Quantity, row.Id, MeteringTime.AsUtc(row.OccurredOnUtc), default);
                folded++;
            }

            return folded;
        }

        private static async Task RouteAsync(IServiceProvider services, Tenant tenant, Site site)
        {
            var tenantManager = services.GetRequiredService<ITenantManager>();
            tenantManager.SetTenant(tenant);
            await tenantManager.SetAliasAsync(tenant.TenantId, site.SiteId);
            if (services.GetRequiredService<Shared.SiteState>().Alias is null)
            {
                tenantManager.SetTenant(tenant); // a site with no alias: SetAliasAsync cleared the routing
            }
        }
    }
}
