#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Health.Persistence;
using Stomme.Modules.Health.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Health.Application
{
    /// <summary>What one rollup pass did, for the job notes.</summary>
    public sealed record HealthRollupResult(int HoursWritten, int DaysWritten, int MinutesSwept);

    /// <summary>
    /// Folds the live Minute series up into Hours and Days, and sweeps whatever the trim-on-write missed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It RECOMPUTES rather than accumulates.</b> Each coarse row is rebuilt from the rows beneath it and
    /// written with <c>Set</c>, so running the pass twice produces the same answer — idempotent by construction
    /// rather than by a "have I done this already" flag. That is the opposite rule from the probe pass, which
    /// ADDS observations to the live minute; the difference is that one is recording an event and the other is
    /// summarising events already recorded.
    /// </para>
    /// <para>
    /// <b>The sweep is a safety net, not the mechanism.</b> The Minute series is trimmed on write by the probe
    /// pass, which is what keeps it bounded even when this job and <c>PurgeJob</c> are both disabled. The sweep
    /// here catches what a gap in ticks left behind: if probing stops for a day, the trim has no writes to
    /// piggy-back on and the rows it would have deleted are stranded.
    /// </para>
    /// </remarks>
    public sealed class HealthRollupPass
    {
        private readonly IHealthRollupRepository _rollups;
        private readonly IUnitOfWork _unitOfWork;
        private readonly TimeProvider _clock;

        public HealthRollupPass(IHealthRollupRepository rollups, IUnitOfWork unitOfWork, TimeProvider? clock = null)
        {
            _rollups = rollups;
            _unitOfWork = unitOfWork;
            _clock = clock ?? TimeProvider.System;
        }

        public async Task<HealthRollupResult> RunAsync(
            TenantId tenantId,
            HealthProbeScope scope,
            int siteId,
            CancellationToken cancellationToken = default)
        {
            var now = _clock.GetUtcNow().UtcDateTime;

            var hours = await FoldAsync(tenantId, scope, siteId, HealthRollupWindow.Minute, HealthRollupWindow.Hour, cancellationToken).ConfigureAwait(false);
            var days = await FoldAsync(tenantId, scope, siteId, HealthRollupWindow.Hour, HealthRollupWindow.Day, cancellationToken).ConfigureAwait(false);
            var swept = await SweepAsync(scope, siteId, now, cancellationToken).ConfigureAwait(false);

            return new HealthRollupResult(hours, days, swept);
        }

        /// <summary>Rebuilds every <paramref name="coarse"/> row from the <paramref name="fine"/> rows under it.</summary>
        private async Task<int> FoldAsync(
            TenantId tenantId,
            HealthProbeScope scope,
            int siteId,
            HealthRollupWindow fine,
            HealthRollupWindow coarse,
            CancellationToken cancellationToken)
        {
            var source = await _rollups.ListAsync(scope, siteId, fine, cancellationToken).ConfigureAwait(false);
            if (source.Count == 0)
            {
                return 0;
            }

            // Grouped in MEMORY by (subject, coarse period). The subject is a string, and string predicates do not
            // agree across the three providers this must run on - so it never appears in a query.
            var groups = source.GroupBy(row => (row.Subject.Value, Period: HealthRollupAggregate.Snap(coarse, row.PeriodStartUtc)));
            var written = 0;

            foreach (var group in groups)
            {
                var totals = HealthRollupAggregate.Downsample(group.ToList());
                if (totals is null)
                {
                    continue;
                }

                var subject = group.First().Subject;
                var id = HealthIds.ForRollup(tenantId, scope, siteId, subject, coarse, group.Key.Period);
                var existing = await _rollups.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);

                if (existing is null)
                {
                    var row = HealthRollupAggregate.For(tenantId, scope, siteId, subject, coarse, group.Key.Period);
                    row.Set(totals.Checks, totals.Healthy, totals.Degraded, totals.Unhealthy, totals.AverageLatencyMs, totals.MaxLatencyMs, totals.AvailabilityPercent);
                    await _rollups.AddAsync(row, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // SET, not accumulate: this is a recomputation of a window from its source rows.
                    existing.Set(totals.Checks, totals.Healthy, totals.Degraded, totals.Unhealthy, totals.AverageLatencyMs, totals.MaxLatencyMs, totals.AvailabilityPercent);
                    await _rollups.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
                }

                written++;
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return written;
        }

        /// <summary>Deletes Minute rows past the live window that the trim-on-write did not reach.</summary>
        private async Task<int> SweepAsync(HealthProbeScope scope, int siteId, DateTime now, CancellationToken cancellationToken)
        {
            var cutoff = HealthRollupAggregate.Snap(HealthRollupWindow.Minute, now - HealthProbePass.MinuteRetention);
            var stale = await _rollups.ListOlderThanAsync(scope, siteId, HealthRollupWindow.Minute, cutoff, cancellationToken).ConfigureAwait(false);
            if (stale.Count == 0)
            {
                return 0;
            }

            foreach (var row in stale)
            {
                await _rollups.DeleteAsync(row, cancellationToken).ConfigureAwait(false);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return stale.Count;
        }
    }
}
