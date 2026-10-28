#nullable enable
using System;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Metering.Domain
{
    /// <summary>
    /// The allowance for one metric on one SITE, and the counter that tracks it (D102).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One authority, calendar-aligned.</b> the predecessor platform ran two: a sliding window folded into a counter, and a
    /// second guard that counted the raw ledger from a calendar month start against a different fallback. They
    /// disagreed by construction. Here the window is calendar-snapped UTC, the counter is authoritative, and the
    /// ledger exists to repair the counter - never to answer in parallel.
    /// </para>
    /// <para>
    /// <b>Computed members are METHODS.</b> <c>IsUnlimited</c> and <c>ConsumedPercent</c> were get-only properties
    /// in the predecessor platform, which one EF context hand-ignored. The reason they cannot stay properties here is not EF: both
    /// document providers serialise read-only properties (their resolvers ignore only DomainEvents), so they would
    /// become stored fields on RavenDB and File with no EF column - a provider divergence invisible in the code.
    /// </para>
    /// </remarks>
    public sealed class QuotaAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        /// <summary>The limit value that means "no limit"; decoded once, in the limit resolver.</summary>
        public const long UnlimitedLimit = -1;

        // Materialisation constructor. Not for application use.
        private QuotaAggregate()
        {
            TenantId = null!;
            Metric = string.Empty;
        }

        private QuotaAggregate(Guid id, TenantId tenantId, int siteId, string metric, QuotaPeriod period, long limit, int warnAtPercent, DateTime periodStartUtc)
        {
            Id = id;
            TenantId = tenantId;
            SiteId = siteId;
            Metric = metric;
            Period = period;
            Limit = limit;
            Unlimited = limit == UnlimitedLimit;
            WarnAtPercent = warnAtPercent;
            PeriodStartUtc = periodStartUtc;
            Status = QuotaStatus.Active;
        }

        public TenantId TenantId { get; private set; }

        /// <summary>The site this quota belongs to - the unit Stomme meters.</summary>
        public int SiteId { get; private set; }

        /// <summary>The canonical metric key; already lowercase, so it is its own normalized column.</summary>
        public string Metric { get; private set; }

        public QuotaPeriod Period { get; private set; }

        /// <summary>The allowance in units, or <see cref="UnlimitedLimit"/>.</summary>
        public long Limit { get; private set; }

        /// <summary>Persisted, not computed: a stored flag survives a limit written by another release.</summary>
        public bool Unlimited { get; private set; }

        /// <summary>The percentage at which a warning is raised once per window; 0 disables it.</summary>
        public int WarnAtPercent { get; private set; }

        /// <summary>Consumption in the current window.</summary>
        public long Consumed { get; private set; }

        /// <summary>The calendar-snapped UTC start of the current window.</summary>
        public DateTime PeriodStartUtc { get; private set; }

        public QuotaStatus Status { get; private set; }

        /// <summary>Extra units granted on top of <see cref="Limit"/> for this window only.</summary>
        public long OverrideAdditional { get; private set; }

        /// <summary>When the override lapses; null when there is none.</summary>
        public DateTime? OverrideExpiresUtc { get; private set; }

        /// <summary>True when a warning has already been raised for the CURRENT window.</summary>
        public bool WarningRaised { get; private set; }

        /// <summary>True when an exceeded notification has already been raised for the CURRENT window.</summary>
        public bool ExceededRaised { get; private set; }

        /// <summary>A method, not a property - see the type remarks.</summary>
        public bool IsUnlimited() => Unlimited || Limit == UnlimitedLimit;

        /// <summary>A method, not a property - see the type remarks. Zero for an unlimited quota.</summary>
        public double ConsumedPercent()
        {
            var effective = EffectiveLimit(DateTime.UtcNow);
            return IsUnlimited() || effective <= 0 ? 0 : (double)Consumed / effective * 100;
        }

        /// <summary>The limit in force at <paramref name="nowUtc"/>, including a live override.</summary>
        public long EffectiveLimit(DateTime nowUtc)
        {
            if (IsUnlimited())
            {
                return UnlimitedLimit;
            }

            var overrideLive = OverrideAdditional > 0 && (OverrideExpiresUtc is null || OverrideExpiresUtc > nowUtc);
            return overrideLive ? Limit + OverrideAdditional : Limit;
        }

        /// <summary>Whether <paramref name="quantity"/> more units would stay within the allowance.</summary>
        public bool HasCapacityFor(long quantity, DateTime nowUtc)
        {
            if (IsUnlimited() || Status == QuotaStatus.Suspended)
            {
                return true;
            }

            var window = WindowStart(Period, nowUtc);
            var consumed = window > PeriodStartUtc ? 0 : Consumed;
            return consumed + quantity <= EffectiveLimit(nowUtc);
        }

        public static QuotaAggregate Define(Guid id, TenantId tenantId, int siteId, string metric, QuotaPeriod period, long limit, int warnAtPercent, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(metric);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A quota belongs to a site.");
            }

            if (limit < 0 && limit != UnlimitedLimit)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), $"A limit is zero or more units, or {UnlimitedLimit} for unlimited.");
            }

            if (warnAtPercent is < 0 or > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(warnAtPercent), "A warning threshold is a percentage.");
            }

            return new QuotaAggregate(id, tenantId, siteId, metric, period, limit, warnAtPercent, WindowStart(period, nowUtc));
        }

        /// <summary>Redefines the allowance, keeping the counter and the window.</summary>
        public void Redefine(long limit, int warnAtPercent)
        {
            if (limit < 0 && limit != UnlimitedLimit)
            {
                throw new ArgumentOutOfRangeException(nameof(limit));
            }

            if (warnAtPercent is < 0 or > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(warnAtPercent));
            }

            Limit = limit;
            Unlimited = limit == UnlimitedLimit;
            WarnAtPercent = warnAtPercent;
        }

        /// <summary>Adds consumption, rolling the window first when the calendar has moved on.</summary>
        /// <returns>True when this fold crossed the warning threshold or the limit for the first time this window.</returns>
        public QuotaCrossing Apply(long quantity, DateTime nowUtc)
        {
            if (quantity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(quantity), "Consumption is positive.");
            }

            RollWindow(nowUtc);

            var before = Consumed;
            Consumed += quantity;

            if (IsUnlimited() || Status == QuotaStatus.Suspended)
            {
                return QuotaCrossing.None;
            }

            var limit = EffectiveLimit(nowUtc);
            if (!ExceededRaised && before <= limit && Consumed > limit)
            {
                ExceededRaised = true;
                return QuotaCrossing.Exceeded;
            }

            if (!WarningRaised && WarnAtPercent > 0 && limit > 0)
            {
                var threshold = (long)Math.Ceiling(limit * (WarnAtPercent / 100d));
                if (before < threshold && Consumed >= threshold)
                {
                    WarningRaised = true;
                    return QuotaCrossing.Warning;
                }
            }

            return QuotaCrossing.None;
        }

        /// <summary>Sets the counter to a recomputed value (the repair pass); never touches a Total quota.</summary>
        public void Repair(long consumed, DateTime windowStartUtc)
        {
            if (Period == QuotaPeriod.Total)
            {
                throw new InvalidOperationException(
                    "A Total quota is never repaired from the ledger: its window never elapses, so a purged ledger would silently restore capacity.");
            }

            if (consumed < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(consumed));
            }

            Consumed = consumed;
            PeriodStartUtc = windowStartUtc;
        }

        /// <summary>Grants extra units for this window.</summary>
        public void GrantOverride(long additional, DateTime? expiresUtc)
        {
            if (additional < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(additional));
            }

            OverrideAdditional = additional;
            OverrideExpiresUtc = expiresUtc;
            ExceededRaised = false;
        }

        public void Suspend() => Status = QuotaStatus.Suspended;

        public void Resume() => Status = QuotaStatus.Active;

        /// <summary>Resets the counter and the window - an operator action, distinct from a window roll.</summary>
        public void Reset(DateTime nowUtc)
        {
            Consumed = 0;
            PeriodStartUtc = WindowStart(Period, nowUtc);
            WarningRaised = false;
            ExceededRaised = false;
        }

        /// <summary>The calendar-snapped UTC start of the window containing <paramref name="nowUtc"/>.</summary>
        public static DateTime WindowStart(QuotaPeriod period, DateTime nowUtc)
        {
            // MeteringTime.AsUtc, never ToUniversalTime: an instant read back from the store carries no kind and is
            // already UTC, and converting it would subtract the machine's offset and move a window boundary.
            var utc = MeteringTime.AsUtc(nowUtc);
            return period switch
            {
                QuotaPeriod.Daily => new DateTime(utc.Year, utc.Month, utc.Day, 0, 0, 0, DateTimeKind.Utc),
                QuotaPeriod.Monthly => new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc),
                _ => DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc)
            };
        }

        private void RollWindow(DateTime nowUtc)
        {
            if (Period == QuotaPeriod.Total)
            {
                return;
            }

            var window = WindowStart(Period, nowUtc);
            if (window > PeriodStartUtc)
            {
                Consumed = 0;
                PeriodStartUtc = window;
                WarningRaised = false;
                ExceededRaised = false;
            }
        }
    }

    /// <summary>What a fold crossed, if anything - raised at most once per window.</summary>
    public enum QuotaCrossing
    {
        None = 0,
        Warning = 1,
        Exceeded = 2
    }
}
