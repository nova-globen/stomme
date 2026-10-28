#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Health;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Health.Domain
{
    /// <summary>
    /// One window's worth of health for one subject: how many checks, how they came out, and how slow they were.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The id is deterministic over (scope, site, subject, window, period start), so a tick that runs twice
    /// recomputes the same row rather than appending a duplicate, and a rollup pass is idempotent by construction
    /// instead of by a "have I done this already" flag.
    /// </para>
    /// <para>
    /// <b>An empty window writes NO row.</b> A row of zeroes and a window nothing ran in are different facts, and
    /// conflating them turns "the probe was never scheduled" into "the probe ran and found nothing wrong" — which
    /// is the exact direction a monitoring bug must never fail in.
    /// </para>
    /// <para>
    /// Every instant here is UTC, and <see cref="HealthTime.AsUtc"/> is how it stays that way. This is not
    /// theoretical: the relational providers hand an instant back with <c>Kind=Unspecified</c>, and calling
    /// <c>ToUniversalTime()</c> on such a value treats it as LOCAL and subtracts the machine offset — which in the
    /// metering module (D102) silently moved usage into the previous day and the previous month's allowance, and
    /// is invisible on a UTC machine such as CI.
    /// </para>
    /// </remarks>
    public sealed class HealthRollupAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        // Materialisation constructor. Not for application use.
        private HealthRollupAggregate()
        {
            TenantId = null!;
            Subject = null!;
        }

        public TenantId TenantId { get; private set; }

        public HealthProbeScope Scope { get; private set; }

        /// <summary>The site, or <c>0</c> for an Installation- or Tenant-scope row.</summary>
        public int SiteId { get; private set; }

        public HealthSubject Subject { get; private set; }

        public HealthRollupWindow Window { get; private set; }

        /// <summary>The UTC start of the window this row covers, snapped to the window's granularity.</summary>
        public DateTime PeriodStartUtc { get; private set; }

        public int Checks { get; private set; }

        public int Healthy { get; private set; }

        public int Degraded { get; private set; }

        public int Unhealthy { get; private set; }

        /// <summary>Mean probe duration in milliseconds, rounded to two places.</summary>
        public double AverageLatencyMs { get; private set; }

        /// <summary>Worst probe duration in milliseconds, rounded to two places.</summary>
        public double MaxLatencyMs { get; private set; }

        /// <summary>Percentage of checks that came out Healthy, rounded to two places.</summary>
        public double AvailabilityPercent { get; private set; }

        /// <summary>
        /// Creates (or names, for an upsert) the row for one window, with the totals already folded.
        /// </summary>
        public static HealthRollupAggregate For(
            TenantId tenantId,
            HealthProbeScope scope,
            int siteId,
            HealthSubject subject,
            HealthRollupWindow window,
            DateTime periodStartUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            ArgumentNullException.ThrowIfNull(subject);
            GuardScope(scope, siteId);

            var start = Snap(window, periodStartUtc);
            return new HealthRollupAggregate
            {
                Id = HealthIds.ForRollup(tenantId, scope, siteId, subject, window, start),
                TenantId = tenantId,
                Scope = scope,
                SiteId = siteId,
                Subject = subject,
                Window = window,
                PeriodStartUtc = start
            };
        }

        /// <summary>
        /// Replaces the totals with a freshly folded set. <b>Set, never accumulate</b> — the pass recomputes a
        /// window from its source rows, so adding to what is already there would double-count every re-run and
        /// make the job's idempotency a lie.
        /// </summary>
        public void Set(int checks, int healthy, int degraded, int unhealthy, double averageLatencyMs, double maxLatencyMs, double availabilityPercent)
        {
            Checks = checks;
            Healthy = healthy;
            Degraded = degraded;
            Unhealthy = unhealthy;
            AverageLatencyMs = averageLatencyMs;
            MaxLatencyMs = maxLatencyMs;
            AvailabilityPercent = availabilityPercent;
        }

        /// <summary>
        /// Merges a freshly observed set of totals INTO this row.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the opposite rule from <see cref="Set"/>, and the difference is which job is calling. The
        /// PROBE job adds observations to the live Minute window: a tick that runs twice in one minute saw two
        /// checks, and overwriting would throw the first away. The ROLLUP job recomputes a coarser window from the
        /// rows underneath it, so it must Set — accumulating there would double-count every re-run and make the
        /// pass's idempotency a lie.
        /// </para>
        /// <para>
        /// The merge uses the same weighted arithmetic as <see cref="Downsample"/>, so a minute built from two
        /// ticks agrees with a minute built from one tick of two checks.
        /// </para>
        /// </remarks>
        public void Accumulate(HealthRollupTotals incoming)
        {
            var checks = Checks + incoming.Checks;
            if (checks == 0)
            {
                return;
            }

            var healthy = Healthy + incoming.Healthy;
            var weighted = ((AverageLatencyMs * Checks) + (incoming.AverageLatencyMs * incoming.Checks)) / checks;

            Checks = checks;
            Healthy = healthy;
            Degraded += incoming.Degraded;
            Unhealthy += incoming.Unhealthy;
            AverageLatencyMs = Math.Round(weighted, 2);
            MaxLatencyMs = Math.Round(Math.Max(MaxLatencyMs, incoming.MaxLatencyMs), 2);
            AvailabilityPercent = Math.Round((double)healthy / checks * 100d, 2);
        }

        /// <summary>
        /// Folds probe results into the totals of one window — the predecessor platform's <c>MetricRollupCalculator</c> math,
        /// lifted: counts by state, mean and worst duration in milliseconds, availability as the healthy
        /// percentage, every figure rounded to two places. Returns <c>null</c> for an empty window, because an
        /// empty window writes no row.
        /// </summary>
        public static HealthRollupTotals? Fold(IReadOnlyCollection<HealthProbeResult> results)
        {
            if (results is null || results.Count == 0)
            {
                return null;
            }

            var checks = results.Count;
            var healthy = results.Count(result => result.State == HealthState.Healthy);
            var degraded = results.Count(result => result.State == HealthState.Degraded);
            var unhealthy = results.Count(result => result.State == HealthState.Unhealthy);

            return new HealthRollupTotals(
                checks,
                healthy,
                degraded,
                unhealthy,
                Math.Round(results.Average(result => result.Duration.TotalMilliseconds), 2),
                Math.Round(results.Max(result => result.Duration.TotalMilliseconds), 2),
                Math.Round((double)healthy / checks * 100d, 2));
        }

        /// <summary>
        /// Folds already-rolled-up rows into a coarser window (Minute rows into an Hour, Hour rows into a Day).
        /// </summary>
        /// <remarks>
        /// <b>Downsampling must agree with folding the raw results directly</b>, or the Day row contradicts the
        /// Hour rows it is made of. Two places that is easy to get wrong and this does not: the average is
        /// weighted by each row's check COUNT rather than averaging the averages, and availability is recomputed
        /// from the summed healthy and total counts rather than averaged.
        /// </remarks>
        public static HealthRollupTotals? Downsample(IReadOnlyCollection<HealthRollupAggregate> rows)
        {
            if (rows is null || rows.Count == 0)
            {
                return null;
            }

            var checks = rows.Sum(row => row.Checks);
            if (checks == 0)
            {
                return null;
            }

            var healthy = rows.Sum(row => row.Healthy);
            var weighted = rows.Sum(row => row.AverageLatencyMs * row.Checks) / checks;

            return new HealthRollupTotals(
                checks,
                healthy,
                rows.Sum(row => row.Degraded),
                rows.Sum(row => row.Unhealthy),
                Math.Round(weighted, 2),
                Math.Round(rows.Max(row => row.MaxLatencyMs), 2),
                Math.Round((double)healthy / checks * 100d, 2));
        }

        /// <summary>Snaps an instant to the start of its window, in UTC.</summary>
        public static DateTime Snap(HealthRollupWindow window, DateTime value)
        {
            var utc = HealthTime.AsUtc(value);
            return window switch
            {
                HealthRollupWindow.Minute => new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc),
                HealthRollupWindow.Hour => new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc),
                HealthRollupWindow.Day => new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc),
                _ => throw new ArgumentOutOfRangeException(nameof(window), window, "Unknown rollup window.")
            };
        }

        /// <summary>
        /// A Site-scope row needs a real site; a non-site row must not pretend to have one. The mock alias the job
        /// path uses carries <c>SiteId = -1</c>, so "negative means installation-wide" would have been a collision
        /// rather than a convention.
        /// </summary>
        internal static void GuardScope(HealthProbeScope scope, int siteId)
        {
            if (scope == HealthProbeScope.Site && siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), siteId, "A Site-scope health row requires a real site id.");
            }

            if (scope != HealthProbeScope.Site && siteId != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), siteId, $"A {scope}-scope health row must carry site id 0.");
            }
        }
    }

    /// <summary>The folded totals of one window, before they are written to a row.</summary>
    public sealed record HealthRollupTotals(
        int Checks,
        int Healthy,
        int Degraded,
        int Unhealthy,
        double AverageLatencyMs,
        double MaxLatencyMs,
        double AvailabilityPercent);
}
