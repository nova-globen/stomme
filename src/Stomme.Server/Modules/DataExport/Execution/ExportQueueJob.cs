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

namespace Stomme.Modules.DataExport.Execution
{
    /// <summary>
    /// The per-minute job that drains every site's export queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It always exists, and does nothing while the feature is off (refutation 4).</b> The framework registers
    /// every <c>IHostedService</c> it finds and inserts its Job row before any enabled check, so this type cannot
    /// be config-gated out of existence: a stock install gains ONE Job row and a per-minute tick whose body
    /// returns at once. <c>IsEnabled</c> is true on the class because it is copied to the row only at first
    /// registration - a row born disabled would stay disabled after the feature was switched on.
    /// </para>
    /// <para>
    /// <b>The scope discipline is the whole job (refutation 3).</b> <c>BeginScope</c> sets the AsyncLocal the module
    /// aggregates read; it is entered BEFORE any fresh DI scope, because the seam's tenant context caches the first
    /// alias per scope. Each site then gets its own DI scope with <c>SetTenant</c> + <c>SetAliasAsync</c>, because the
    /// framework's File, Folder and Log repositories read <c>SiteState</c> instead - and
    /// <c>FolderRepository.GetFolderPathAsync</c> builds the PHYSICAL path from the resolved tenant. Get this wrong
    /// and one tenant's export is written under another tenant's directory.
    /// </para>
    /// <para>
    /// A site that throws is logged and skipped; the next site still drains.
    /// </para>
    /// <para>
    /// <b><c>MaximumDuration</c> is the LEASE, not the render timeout - and it is NOT reconciled onto an existing row
    /// (refutation 4's tail, settled 2026-09-21).</b> The framework's auto-heal clears a stuck <c>IsExecuting</c> once a
    /// tick is <c>MaximumDuration</c> overdue. For most jobs that flag is what stops a second run, so it must outlast the
    /// longest run. Here it is not: exactly-once is the job ROW's lease, raced on three providers, so a second tick
    /// beside a live one is safe - and a LARGER value only lengthens how long exports stall after a host is killed
    /// mid-tick. So the class default is the default lease (10 minutes), not <c>JobTimeoutMinutes</c>. It reaches the
    /// Job row at first registration only, and that is deliberate: the row is operator-editable in admin/jobs, and
    /// re-applying a default on every boot would silently undo an operator's change - the lesson
    /// <c>AdminSiteTemplate</c>'s reconciliation already paid for. An install that registered the row before this
    /// (30 minutes) keeps it; the only cost is a slower recovery after a crash.
    /// </para>
    /// </remarks>
    public class ExportQueueJob : HostedServiceBase
    {
        // JobType = "Stomme.Modules.DataExport.Execution.ExportQueueJob, Stomme.Server"

        public ExportQueueJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Data Export Queue Job";
            Frequency = "m"; // minute
            Interval = 1;
            IsEnabled = true; // gated at run time by DataExport:Enabled - see the remarks
            MaximumDuration = new DataExportOptions().LeaseMinutes; // the lease, not the render timeout - see the remarks
        }

        public override async Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            if (!provider.GetRequiredService<DataExportOptions>().Enabled)
            {
                return "Data Export Is Disabled (" + DataExportOptions.SectionName + ":Enabled)<br />";
            }

            var tenant = await provider.GetRequiredService<ITenantManager>().GetTenantAsync();
            var tenantId = tenant is null ? null : FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId);
            if (tenantId is null)
            {
                return "Data Export: No Tenant Is Set For This Run<br />";
            }

            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var logger = provider.GetRequiredService<ILogger<ExportQueueJob>>();

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
                    if (siteScope.ServiceProvider.GetRequiredService<Shared.SiteState>().Alias is null)
                    {
                        tenantManager.SetTenant(tenant);
                    }

                    // Due schedules queue their runs first, so a run is rendered in this same tick.
                    var scheduled = await siteScope.ServiceProvider.GetRequiredService<ExportSchedulePass>().RunSiteAsync(site.SiteId);
                    if (scheduled != new ExportScheduleResult(0, 0, 0))
                    {
                        log.Append("Data Export Schedules For Site: ").Append(site.Name)
                            .Append($" - {scheduled.Queued} Queued, {scheduled.Paused} Paused, {scheduled.ClaimedElsewhere} Claimed Elsewhere<br />");
                    }

                    var result = await siteScope.ServiceProvider.GetRequiredService<ExportQueueProcessor>().DrainSiteAsync(site.SiteId);

                    // Every tick rather than hourly: with nothing due it is one indexed query, and an artifact then stops
                    // being on disk within a minute of expiring rather than within an hour.
                    var retired = await siteScope.ServiceProvider.GetRequiredService<ExportExpiryPass>().RunSiteAsync(site.SiteId);
                    if (retired != new ExportExpiryResult(0, 0, 0, 0, 0))
                    {
                        log.Append("Data Export Retention For Site: ").Append(site.Name)
                            .Append($" - {retired.Expired} Expired ({retired.ArtifactsDeleted} Deleted, {retired.ArtifactsLeftAlone} Left Alone), {retired.AuditPurged} Audit Row(s) Purged, {retired.TempFilesSwept} Temp File(s) Swept<br />");
                    }

                    if (result != new ExportDrainResult(0, 0, 0, 0))
                    {
                        log.Append("Data Export For Site: ").Append(site.Name)
                            .Append($" - {result.Completed} Completed, {result.Failed} Failed, {result.Abandoned} Abandoned, {result.LostRace} Claimed Elsewhere<br />");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "The export queue failed for site {SiteName} ({SiteId}) of tenant {TenantId}.", site.Name, site.SiteId, tenant!.TenantId);
                    log.Append("Data Export Failed For Site: ").Append(site.Name).Append(" - ").Append(ex.GetType().Name).Append("<br />");
                }
            }

            return log.ToString();
        }
    }
}
