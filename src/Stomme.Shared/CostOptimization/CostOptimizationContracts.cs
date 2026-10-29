#nullable enable
using System;
using System.Collections.Generic;

namespace Stomme.CostOptimization
{
    // ============================================================================================================
    // CostOptimization's Shared contracts: the
    // category and lifecycle enums, the keys, and what the API and the page read. BCL-only like every Shared contract.
    // ============================================================================================================

    /// <summary>
    /// The kind of savings opportunity a recommendation addresses (the predecessor platform's, lifted). Values start at 1 so the CLR default
    /// is not a category and the domain's raise refuses it.
    /// </summary>
    public enum RecommendationCategory
    {
        /// <summary>Route AI requests to a cheaper model tier appropriate for their complexity.</summary>
        ModelTiering = 1,

        /// <summary>Serve repeated deterministic AI responses from the cache instead of re-invoking the provider.</summary>
        Caching = 2,

        /// <summary>Remove idle or orphaned resources (stale logs, temp files, soft-deleted leftovers).</summary>
        Cleanup = 3,

        /// <summary>Resize an over-provisioned resource, schedule, or quota to match actual demand.</summary>
        RightSizing = 4,
    }

    /// <summary>The one-way lifecycle of a recommendation: Open, then Applied or Dismissed (the predecessor platform's).</summary>
    public enum RecommendationStatus
    {
        /// <summary>Raised and awaiting a decision.</summary>
        Open = 1,

        /// <summary>Accepted; the action taken is recorded.</summary>
        Applied = 2,

        /// <summary>Rejected; its key is suppressed until the dismissal cooldown ends.</summary>
        Dismissed = 3,
    }

    /// <summary>The module's keys (the predecessor platform's names), declared to Administrators as every Wave 2 and Wave 5 module's are.</summary>
    public static class CostOptimizationPermissions
    {
        public const string Resource = "cost-optimization";

        public const string View = "cost-optimization:view";

        public const string Manage = "cost-optimization:manage";

        public static readonly IReadOnlyList<string> All = new[] { View, Manage };

        public static readonly IReadOnlyList<(string Key, string[] Roles)> Declarations = new (string Key, string[] Roles)[]
        {
            (View, new[] { "Administrators" }),
            (Manage, new[] { "Administrators" }),
        };
    }

    /// <summary>
    /// One recommendation as the advisor surfaces it (the predecessor platform's <c>RecommendationView</c>). CHANGED: the instants are UTC
    /// <see cref="DateTime"/> values, as every module store holds them.
    /// </summary>
    public sealed record RecommendationView(
        Guid Id,
        RecommendationCategory Category,
        string RecommendationKey,
        string Summary,
        IReadOnlyList<string> Evidence,
        decimal EstimatedSavingAmount,
        string EstimatedSavingCurrency,
        RecommendationStatus Status,
        DateTime RaisedUtc,
        string? AppliedAction,
        DateTime? AppliedUtc,
        string? DismissReason,
        DateTime? DismissedUntilUtc);

    /// <summary>
    /// The outcome of applying or dismissing a recommendation. CHANGED from the predecessor platform's <c>Result&lt;RecommendationView&gt;</c>,
    /// which Stomme does not have: a refusal carries the predecessor platform's code (<c>not-found</c>, <c>not-open</c>,
    /// <c>invalid-cooldown</c>) and a reason; a success carries the decided recommendation.
    /// </summary>
    public sealed record RecommendationDecisionView(bool Success, string? Code, string? Reason, RecommendationView? Recommendation)
    {
        public const string NotFound = "not-found";

        public const string NotOpen = "not-open";

        public const string InvalidCooldown = "invalid-cooldown";

        public static RecommendationDecisionView Decided(RecommendationView recommendation) => new(true, null, null, recommendation);

        public static RecommendationDecisionView Refused(string code, string reason) => new(false, code, reason, null);
    }

    /// <summary>
    /// One advisor pass (the predecessor platform's): how many recommendations it raised, their keys in analyzer order, and how many drafts it
    /// suppressed because their key was already open, cooling down, or drafted twice in the pass.
    /// </summary>
    public sealed record AdvisorRunView(int RaisedCount, IReadOnlyList<string> RaisedKeys, int SuppressedCount);

    /// <summary>The body of <c>POST api/CostOptimization/{id}/apply</c>: what was done.</summary>
    public sealed class ApplyRecommendationRequest
    {
        public string ActionTaken { get; set; } = string.Empty;
    }

    /// <summary>The body of <c>POST api/CostOptimization/{id}/dismiss</c>: why, and optionally for how many days the key is suppressed.</summary>
    public sealed class DismissRecommendationRequest
    {
        public string Reason { get; set; } = string.Empty;

        public int? CooldownDays { get; set; }
    }
}
