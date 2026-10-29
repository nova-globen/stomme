#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Stomme.Modules.CostOptimization.Domain;

// Lifted from the predecessor platform: the rules, thresholds, keys and evidence are unchanged. CHANGED: the
// thresholds arrive as the host-read AdvisorOptions rather than IOptions, and a signal source reads the
// REQUEST's site - so a draft is a site's.
namespace Stomme.Modules.CostOptimization.Application.Advisors
{
    /// <summary>
    /// Flags a model that carries a high share of the month's AI spend while its average tokens per
    /// request sit inside the Simple-task budget — the signature of an expensive model doing work the
    /// tier router (<c>CostOptimization:ModelTiers</c>) could send to a cheaper one. The estimated saving is
    /// the flagged model's monthly spend times the configured, evidence-cited
    /// <see cref="AdvisorOptions.ModelTieringSavingRate"/> assumption.
    /// </summary>
    public sealed class ExpensiveModelForSimpleTaskAnalyzer(
        AdvisorOptions options,
        IAssistantUsageSignalSource? usageSignals = null) : IRecommendationAnalyzer
    {
        /// <inheritdoc />
        public async Task<IReadOnlyList<RecommendationDraft>> AnalyzeAsync(CancellationToken ct = default)
        {
            if (usageSignals is null)
            {
                return [];
            }

            var thresholds = options;
            var usage = await usageSignals.GetCurrentMonthUsageAsync(ct);
            var totalCost = usage.ByModel.Sum(m => m.EstimatedCost);
            if (totalCost <= 0m)
            {
                return [];
            }

            var drafts = new List<RecommendationDraft>();
            foreach (var model in usage.ByModel)
            {
                if (model.RequestCount < thresholds.ExpensiveModelMinRequests || model.EstimatedCost <= 0m)
                {
                    continue;
                }

                var avgTokens = model.TotalTokens / model.RequestCount;
                var costShare = model.EstimatedCost / totalCost;
                if (avgTokens > thresholds.SimpleTaskMaxAvgTokens || costShare < thresholds.ExpensiveModelMinCostShare)
                {
                    continue;
                }

                var saving = decimal.Round(model.EstimatedCost * thresholds.ModelTieringSavingRate, 2);
                drafts.Add(new RecommendationDraft(
                    RecommendationCategory.ModelTiering,
                    Invariant($"model-tiering:{model.Model.ToLowerInvariant()}:simple-tasks"),
                    Invariant($"Route {model.Model}'s simple-task traffic to a cheaper model tier."),
                    [
                        Invariant($"{usage.Month}: {model.Provider}/{model.Model} served {model.RequestCount} requests averaging {avgTokens} tokens per request (Simple-task budget: {thresholds.SimpleTaskMaxAvgTokens})."),
                        Invariant($"{usage.Month}: {model.Provider}/{model.Model} cost {model.EstimatedCost} {usage.Currency} — {decimal.Round(costShare * 100m, 1)}% of the month's AI spend (flag threshold: {thresholds.ExpensiveModelMinCostShare * 100m}%)."),
                        Invariant($"Estimated saving assumes tier routing cuts {thresholds.ModelTieringSavingRate * 100m}% of this model's monthly spend (configurable assumption)."),
                    ],
                    EstimatedSaving.Of(saving, usage.Currency)));
            }

            return drafts;
        }

        private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Flags a month whose cache-served share of AI requests sits below the configured floor despite
    /// meaningful request volume — the signal to enable or tune the deterministic LLM response cache
    /// (<c>CostOptimization:ResponseCache</c>). The estimated saving is the spend the gap to the floor
    /// represents: monthly AI spend times (floor − actual cached share).
    /// </summary>
    public sealed class HighCacheMissAnalyzer(
        AdvisorOptions options,
        IAssistantUsageSignalSource? usageSignals = null) : IRecommendationAnalyzer
    {
        /// <inheritdoc />
        public async Task<IReadOnlyList<RecommendationDraft>> AnalyzeAsync(CancellationToken ct = default)
        {
            if (usageSignals is null)
            {
                return [];
            }

            var thresholds = options;
            var usage = await usageSignals.GetCurrentMonthUsageAsync(ct);
            var requests = usage.ByModel.Sum(m => m.RequestCount);
            if (requests < thresholds.CacheMissMinRequests)
            {
                return [];
            }

            var cached = usage.ByModel.Sum(m => m.CachedRequestCount);
            var cachedShare = (decimal)cached / requests;
            if (cachedShare >= thresholds.MinCachedHitShare)
            {
                return [];
            }

            var totalCost = usage.ByModel.Sum(m => m.EstimatedCost);
            var saving = decimal.Round(totalCost * (thresholds.MinCachedHitShare - cachedShare), 2);
            return
            [
                new RecommendationDraft(
                    RecommendationCategory.Caching,
                    "caching:assistant:low-hit-rate",
                    "Enable or tune the deterministic LLM response cache to lift the cache-hit share.",
                    [
                        Invariant($"{usage.Month}: {cached} of {requests} AI requests were served from the response cache — a {decimal.Round(cachedShare * 100m, 1)}% hit share (floor: {thresholds.MinCachedHitShare * 100m}%)."),
                        Invariant($"{usage.Month}: AI spend was {totalCost} {usage.Currency}; estimated saving is the spend share of the gap to the {thresholds.MinCachedHitShare * 100m}% floor."),
                    ],
                    EstimatedSaving.Of(Math.Max(0m, saving), usage.Currency)),
            ];
        }

        private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Flags a month whose consumed share of the tenant's AI request quota has reached the configured
    /// warning level, so the tenant can right-size the quota (or cut request volume) before requests are
    /// refused. Unlimited tenants carry no quota signal and are never flagged. Carries a zero saving —
    /// this is a right-sizing advisory, not a measured spend cut.
    /// </summary>
    public sealed class QuotaApproachingAnalyzer(
        AdvisorOptions options,
        IAssistantUsageSignalSource? usageSignals = null) : IRecommendationAnalyzer
    {
        /// <inheritdoc />
        public async Task<IReadOnlyList<RecommendationDraft>> AnalyzeAsync(CancellationToken ct = default)
        {
            if (usageSignals is null)
            {
                return [];
            }

            var thresholds = options;
            var usage = await usageSignals.GetCurrentMonthUsageAsync(ct);
            if (usage.Quota is not { Limit: > 0 } quota)
            {
                return [];
            }

            var usedShare = (decimal)quota.Consumed / quota.Limit;
            if (usedShare < thresholds.QuotaApproachingMinUsedShare)
            {
                return [];
            }

            return
            [
                new RecommendationDraft(
                    RecommendationCategory.RightSizing,
                    "right-sizing:ai-quota:approaching",
                    "The monthly AI request quota is nearly consumed — right-size the quota or reduce request volume.",
                    [
                        Invariant($"{usage.Month}: {quota.Consumed} of {quota.Limit} monthly AI requests consumed — {decimal.Round(usedShare * 100m, 1)}% of the quota (warning level: {thresholds.QuotaApproachingMinUsedShare * 100m}%)."),
                    ],
                    EstimatedSaving.Of(0m, usage.Currency)),
            ];
        }

        private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
