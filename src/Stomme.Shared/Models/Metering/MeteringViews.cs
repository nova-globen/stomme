using System;
using System.Collections.Generic;

namespace Stomme.Models.Metering
{
    /// <summary>
    /// The wire shapes the metering UI renders (D102 item 11). They are deliberately FLAT and carry no aggregate
    /// type: the Client never sees the metering domain, which is also what the dependency-direction gate asserts.
    /// </summary>
    /// <remarks>
    /// The computed values that are METHODS on the aggregates (<c>IsUnlimited()</c>, <c>ConsumedPercent()</c>,
    /// <c>BillableTokens()</c>) are materialised here as ordinary properties, because a view model is serialised on
    /// purpose - the reason they are methods on the aggregate is precisely that the document stores would persist
    /// them, which does not apply to a DTO that is never stored.
    /// </remarks>
    public class QuotaView
    {
        public Guid QuotaId { get; set; }

        public string Metric { get; set; }

        /// <summary>"Total", "Daily" or "Monthly" - the string the wire carries, so the Client needs no domain enum.</summary>
        public string Period { get; set; }

        public long Limit { get; set; }

        public bool Unlimited { get; set; }

        public int WarnAtPercent { get; set; }

        public long Consumed { get; set; }

        public long Remaining { get; set; }

        public double ConsumedPercent { get; set; }

        public DateTime PeriodStartUtc { get; set; }

        /// <summary>"Active" or "Suspended".</summary>
        public string Status { get; set; }

        public long OverrideAdditional { get; set; }

        public DateTime? OverrideExpiresUtc { get; set; }
    }

    /// <summary>One row of the usage ledger, as the log renders it.</summary>
    public class UsageRecordView
    {
        public Guid UsageId { get; set; }

        public string Metric { get; set; }

        public long Quantity { get; set; }

        public int? UserId { get; set; }

        public string Source { get; set; }

        public DateTime OccurredOnUtc { get; set; }

        /// <summary>"Pending" or "Applied" - a Pending row older than the grace period is the recovery job's work.</summary>
        public string FoldState { get; set; }
    }

    /// <summary>The caller's own consumption on this site: one line per metric, with the quota it counts against.</summary>
    public class MyUsageLine
    {
        public string Metric { get; set; }

        /// <summary>What THIS user consumed in the quota's current window (or in total where there is no quota).</summary>
        public long MyConsumption { get; set; }

        /// <summary>The site-wide consumption of the quota's window; null when the metric has no quota.</summary>
        public long? SiteConsumption { get; set; }

        public long? Limit { get; set; }

        public bool Unlimited { get; set; }

        public string Period { get; set; }
    }

    /// <summary>One day-and-slice of AI consumption, from the rollups the nightly pass maintains.</summary>
    public class AiUsageRollupView
    {
        public DateTime DayUtc { get; set; }

        public string Provider { get; set; }

        public string Model { get; set; }

        public string Agent { get; set; }

        public long Calls { get; set; }

        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        public long BillableTokens { get; set; }

        public long Errors { get; set; }

        public decimal EstimatedCost { get; set; }

        public string CurrencyCode { get; set; }
    }

    /// <summary>One AI call, as the dashboard's detail list renders it. Never a prompt or a completion.</summary>
    public class AiUsageRecordView
    {
        public Guid AiUsageId { get; set; }

        public string Provider { get; set; }

        public string Model { get; set; }

        public string Agent { get; set; }

        public long InputTokens { get; set; }

        public long OutputTokens { get; set; }

        public long LatencyMs { get; set; }

        /// <summary>"Success", "Error" or "Cached".</summary>
        public string Status { get; set; }

        public decimal EstimatedCost { get; set; }

        public string CurrencyCode { get; set; }

        public double? QualityScore { get; set; }

        public int? UserId { get; set; }

        public DateTime OccurredOnUtc { get; set; }
    }

    /// <summary>What an operator posts to define or redefine a quota. The SITE is never on the wire - it is resolved.</summary>
    public class QuotaDefinition
    {
        public string Metric { get; set; }

        public string Period { get; set; } = "Monthly";

        public long Limit { get; set; }

        public int WarnAtPercent { get; set; } = 80;
    }

    /// <summary>What an operator posts to grant extra units for the current window.</summary>
    public class QuotaOverrideRequest
    {
        public long Additional { get; set; }

        public DateTime? ExpiresUtc { get; set; }
    }

    /// <summary>The AI dashboard: the rollups, plus the totals the header renders.</summary>
    public class AiUsageDashboard
    {
        public IList<AiUsageRollupView> Rollups { get; set; } = new List<AiUsageRollupView>();

        public long Calls { get; set; }

        public long BillableTokens { get; set; }

        public long Errors { get; set; }

        public decimal EstimatedCost { get; set; }

        public string CurrencyCode { get; set; } = "USD";
    }
}
