#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Infrastructure.Tenancy;
using Stomme.Metering;
using Stomme.Models;
using Stomme.Modules.Metering.Application;
using Stomme.Modules.Metering.Domain;
using Stomme.Metering.Persistence;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Metering.Infrastructure
{
    /// <summary>What one site's rollup pass did - the numbers the job log reports and the tests assert on.</summary>
    public sealed record RollupPassResult(int RollupsWritten, int RollupsDeleted, int QuotasRepaired, int LedgerPurged, int AiLedgerPurged, int RollupsPurged);

    /// <summary>
    /// The nightly pass: AI rollups, the counter repair, and retention.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Idempotent by deterministic id, incremental by window.</b> the predecessor platform's rollup job rescanned the ENTIRE
    /// ledger every night and never deleted a rollup whose rows had gone, so the dashboard slowly filled with
    /// slices no ledger supported. Here each slice's row id is derived from (site, day, provider, model, agent), a
    /// re-run recomputes the same rows rather than appending duplicates, only days touched since the last pass are
    /// recomputed, and a rollup inside the recomputed range with no surviving ledger rows is DELETED.
    /// </para>
    /// <para>
    /// <b>The repair pass has two refusals, both binding.</b> It never touches a <see cref="QuotaPeriod.Total"/>
    /// quota - that window never elapses, so recomputing it from a ledger that retention has trimmed would silently
    /// hand the site back capacity it had already spent. And it repairs a Daily or Monthly window only when the
    /// RETAINED ledger covers that window completely; a window that begins before the retention cutoff is skipped,
    /// because a partial re-count is an under-count wearing the clothes of a correction.
    /// </para>
    /// <para>
    /// <b>Order.</b> Rollups and repair run BEFORE retention, on the same pass and with the same clock: purging
    /// first would remove the very rows the repair is entitled to count.
    /// </para>
    /// </remarks>
    public class UsageRollupJob : HostedServiceBase
    {
        // JobType = "Stomme.Modules.Metering.Infrastructure.UsageRollupJob, Stomme.Server"

        /// <summary>How far back a first pass (no recorded previous pass) recomputes.</summary>
        internal const int FirstPassDays = 7;

        /// <summary>Always recompute at least this many days back, so a late-arriving row is not stranded.</summary>
        internal const int OverlapDays = 2;

        public UsageRollupJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Usage Rollup Job";
            Frequency = "d"; // day
            Interval = 1;
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
                return "Usage Rollup: No Tenant Is Set For This Run<br />";
            }

            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var logger = provider.GetRequiredService<ILogger<UsageRollupJob>>();

            // The tenant's AMBIENT scope is entered BEFORE any fresh DI scope: the seam's tenant context caches the
            // first alias per scope, so module reads for tenant 2 would otherwise be filtered as tenant 1.
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

                    var result = await siteScope.ServiceProvider.GetRequiredService<UsageRollupPass>().RunSiteAsync(site.SiteId, tenantId);
                    log.Append("Usage Rollup For Site: ").Append(site.Name)
                        .Append($" - Rollups {result.RollupsWritten} Written, {result.RollupsDeleted} Removed; Quotas Repaired {result.QuotasRepaired}; ")
                        .Append($"Purged {result.LedgerPurged} Usage, {result.AiLedgerPurged} AI, {result.RollupsPurged} Rollup Row(s)<br />");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "The usage rollup failed for site {SiteName} ({SiteId}) of tenant {TenantId}.", site.Name, site.SiteId, tenant!.TenantId);
                    log.Append("Usage Rollup Failed For Site: ").Append(site.Name).Append(" - ").Append(ex.GetType().Name).Append("<br />");
                }
            }

            return log.ToString();
        }
    }

    /// <summary>
    /// The rollup pass itself, separated from the job so a test drives it against a site without a hosted service
    /// (the <c>NotificationDispatchProcessor</c> shape). Scoped: it is resolved inside the site's own DI scope.
    /// </summary>
    public sealed class UsageRollupPass
    {
        private readonly IUsageRecordRepository _usage;
        private readonly IAiUsageRecordRepository _aiUsage;
        private readonly IAiUsageRollupRepository _rollups;
        private readonly IQuotaRepository _quotas;
        private readonly IMeteringSiteSettings _settings;
        private readonly IUnitOfWork _unitOfWork;
        private readonly IMeteringClock _clock;
        private readonly ILogger<UsageRollupPass> _logger;

        public UsageRollupPass(
            IUsageRecordRepository usage,
            IAiUsageRecordRepository aiUsage,
            IAiUsageRollupRepository rollups,
            IQuotaRepository quotas,
            IMeteringSiteSettings settings,
            IUnitOfWork unitOfWork,
            IMeteringClock clock,
            ILogger<UsageRollupPass> logger)
        {
            _usage = usage;
            _aiUsage = aiUsage;
            _rollups = rollups;
            _quotas = quotas;
            _settings = settings;
            _unitOfWork = unitOfWork;
            _clock = clock;
            _logger = logger;
        }

        public async Task<RollupPassResult> RunSiteAsync(int siteId, Tenancy.Domain.TenantId tenantId, CancellationToken ct = default)
        {
            var nowUtc = _clock.UtcNow;
            var lastPass = await _settings.GetLastRollupPassAsync(siteId, ct);
            var from = DayStart(lastPass?.AddDays(-UsageRollupJob.OverlapDays) ?? nowUtc.AddDays(-UsageRollupJob.FirstPassDays));

            var (written, deleted) = await RollUpAiUsageAsync(siteId, tenantId, from, ct);
            var repaired = await RepairQuotasAsync(siteId, nowUtc, ct);
            var (ledgerPurged, aiPurged, rollupsPurged) = await PurgeAsync(siteId, nowUtc, ct);

            await _settings.SetLastRollupPassAsync(siteId, nowUtc, ct);
            return new RollupPassResult(written, deleted, repaired, ledgerPurged, aiPurged, rollupsPurged);
        }

        /// <summary>Recomputes every touched day's slices from the ledger, and removes slices the ledger no longer supports.</summary>
        private async Task<(int Written, int Deleted)> RollUpAiUsageAsync(int siteId, Tenancy.Domain.TenantId tenantId, DateTime fromUtc, CancellationToken ct)
        {
            var rows = await _aiUsage.ListSinceAsync(siteId, fromUtc, ct);
            var existing = await _rollups.ListSinceAsync(siteId, fromUtc, ct);

            var computed = rows
                .GroupBy(row => (Day: DayStart(row.OccurredOnUtc), row.ProviderNormalized, row.ModelNormalized, row.AgentNormalized))
                .ToList();

            var written = 0;
            var live = new HashSet<Guid>();
            foreach (var slice in computed)
            {
                var id = AiUsageRollupAggregate.IdFor(tenantId, siteId, slice.Key.Day, slice.Key.ProviderNormalized, slice.Key.ModelNormalized, slice.Key.AgentNormalized);
                live.Add(id);

                var calls = slice.LongCount();
                // Billable tokens only: a cache hit is a call, and its tokens were never bought (AiUsageRecordAggregate).
                var input = slice.Where(row => row.Status != AiCallStatus.Cached).Sum(row => row.InputTokens);
                var output = slice.Where(row => row.Status != AiCallStatus.Cached).Sum(row => row.OutputTokens);
                var errors = slice.LongCount(row => row.Status == AiCallStatus.Error);
                var cost = slice.Sum(row => row.EstimatedCost);
                var currency = slice.Select(row => row.CurrencyCode).FirstOrDefault() ?? "USD";

                var rollup = await _rollups.GetByIdAsync(id, ct);
                if (rollup is null)
                {
                    rollup = AiUsageRollupAggregate.For(tenantId, siteId, slice.Key.Day, slice.Key.ProviderNormalized, slice.Key.ModelNormalized, slice.Key.AgentNormalized, currency);
                    rollup.Set(calls, input, output, errors, cost);
                    await _rollups.AddAsync(rollup, ct);
                    written++;
                    continue;
                }

                // Set, never accumulate: a re-run must converge on the ledger's own totals.
                rollup.Set(calls, input, output, errors, cost);
                await _rollups.UpdateAsync(rollup, ct);
                written++;
            }

            var deleted = 0;
            foreach (var orphan in existing.Where(row => !live.Contains(row.Id)))
            {
                // Only inside the recomputed range: outside it the absence of rows proves nothing.
                await _rollups.DeleteAsync(orphan, ct);
                deleted++;
            }

            if (written > 0 || deleted > 0)
            {
                await _unitOfWork.SaveChangesAsync(ct);
            }

            return (written, deleted);
        }

        /// <summary>Re-counts Daily and Monthly counters from the ledger, and only where the retained ledger covers the window.</summary>
        private async Task<int> RepairQuotasAsync(int siteId, DateTime nowUtc, CancellationToken ct)
        {
            var retentionCutoff = nowUtc.AddDays(-await _settings.GetUsageRetentionDaysAsync(siteId, ct));
            var repaired = 0;

            foreach (var quota in await _quotas.ListBySiteAsync(siteId, ct))
            {
                if (quota.Period == QuotaPeriod.Total)
                {
                    continue; // never repaired - see the job remarks
                }

                var windowStart = QuotaAggregate.WindowStart(quota.Period, nowUtc);
                if (windowStart < retentionCutoff)
                {
                    _logger.LogInformation(
                        "The {Period} quota for '{Metric}' on site {SiteId} is not repaired: its window starts before the retention cutoff, so the retained ledger does not cover it.",
                        quota.Period, quota.Metric, siteId);
                    continue;
                }

                var counted = (await _usage.ListForWindowAsync(siteId, quota.Metric, windowStart, nowUtc.AddSeconds(1), ct))
                    .Where(row => row.FoldState == FoldState.Applied)
                    .Sum(row => row.Quantity);

                if (counted == quota.Consumed && quota.PeriodStartUtc == windowStart)
                {
                    continue;
                }

                quota.Repair(counted, windowStart);

                try
                {
                    await _quotas.UpdateAsync(quota, ct);
                    await _unitOfWork.SaveChangesAsync(ct);
                    repaired++;
                }
                catch (ConcurrencyConflictException)
                {
                    // A LIVE FOLD updated this quota while the repair was recomputing it, so the value just
                    // computed is already stale and the fold's is authoritative. Skipping is the correct
                    // outcome, not a failure - and this is not rare: the repair reads every quota on the site
                    // while requests keep arriving.
                    //
                    // MEASURED, and the reason this is now per-quota: the pass used to update every quota and
                    // save ONCE at the end, so a single conflicting row lost the whole site's repair and the
                    // job's per-site catch logged it at Error. The drive caught it as a host error on
                    // PostgreSQL. One quota losing a race must not cost the others theirs.
                    //
                    // This guard only works because the REPOSITORY leaves the context usable after a failed save
                    // (EfRepository.DiscardFailedWrite). Until that landed, the first conflict poisoned
                    // the scope's DbContext and every LATER quota in this loop threw too — and was logged, right
                    // here, as though a live fold had beaten it. Measured by
                    // SaveOncePassContainmentTests; if that suite ever goes red, this message starts lying again.
                    _logger.LogInformation(
                        "The {Period} quota for '{Metric}' on site {SiteId} was updated by a live fold while the repair " +
                        "was recomputing it; the fold's value is authoritative and the repair is skipped for this run.",
                        quota.Period, quota.Metric, siteId);
                }
            }

            return repaired;
        }

        /// <summary>Retention, per SITE only - never a whole-store sweep.</summary>
        private async Task<(int Ledger, int AiLedger, int Rollups)> PurgeAsync(int siteId, DateTime nowUtc, CancellationToken ct)
        {
            var usageCutoff = nowUtc.AddDays(-await _settings.GetUsageRetentionDaysAsync(siteId, ct));
            var rollupCutoff = DayStart(nowUtc.AddDays(-await _settings.GetRollupRetentionDaysAsync(siteId, ct)));

            var ledger = await _usage.ListOlderThanAsync(siteId, usageCutoff, ct);
            foreach (var row in ledger)
            {
                await _usage.DeleteAsync(row, ct);
            }

            var aiLedger = await _aiUsage.ListOlderThanAsync(siteId, usageCutoff, ct);
            foreach (var row in aiLedger)
            {
                await _aiUsage.DeleteAsync(row, ct);
            }

            var rollups = await _rollups.ListOlderThanAsync(siteId, rollupCutoff, ct);
            foreach (var row in rollups)
            {
                await _rollups.DeleteAsync(row, ct);
            }

            if (ledger.Count + aiLedger.Count + rollups.Count > 0)
            {
                await _unitOfWork.SaveChangesAsync(ct);
            }

            return (ledger.Count, aiLedger.Count, rollups.Count);
        }

        // The rollup groups by DAY, so the conversion applied to a stored instant decides which day a call lands in:
        // see MeteringTime, where using ToUniversalTime here split two calls an hour apart across two days.
        private static DateTime DayStart(DateTime value) => MeteringTime.DayStart(value);
    }
}
