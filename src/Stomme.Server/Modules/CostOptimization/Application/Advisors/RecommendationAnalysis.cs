#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Stomme.Modules.CostOptimization.Domain;
using Microsoft.Extensions.Configuration;

namespace Stomme.Modules.CostOptimization.Application.Advisors
{
    /// <summary>
    /// One savings opportunity an analyzer found (the predecessor platform's <c>RecommendationDraft</c>): everything a recommendation needs
    /// except the tenant, the site and the instant, which the raise command stamps. The key is the finding's deterministic
    /// identity - the command deduplicates drafts by it against open and cooling-down recommendations.
    /// </summary>
    public sealed record RecommendationDraft(
        RecommendationCategory Category,
        string RecommendationKey,
        string Summary,
        IReadOnlyList<string> Evidence,
        EstimatedSaving EstimatedSaving);

    /// <summary>
    /// One advisor rule (the predecessor platform's): reads its signal source(s) for the request's site and drafts evidence-cited
    /// recommendations. Rule-threshold based (<see cref="AdvisorOptions"/>), and it drafts NOTHING - never a guess - when
    /// its signal source is not composed into the host.
    /// </summary>
    public interface IRecommendationAnalyzer
    {
        Task<IReadOnlyList<RecommendationDraft>> AnalyzeAsync(CancellationToken ct = default);
    }

    /// <summary>
    /// The advisor's thresholds and the dismissal default (the predecessor platform's <c>AdvisorOptions</c>), read from the HOST's
    /// configuration section <c>CostOptimization:Advisor</c> (never the container's <c>IConfiguration</c>). Every
    /// value has the predecessor platform's default; the savings RATES are declared assumptions the analyzers cite in their evidence, not
    /// measurements. CHANGED: the section drops the predecessor platform's <c>the predecessor platform:</c> prefix, as every lifted module's does.
    /// </summary>
    public sealed class AdvisorOptions
    {
        public const string SectionName = "CostOptimization:Advisor";

        /// <summary>A model whose average tokens per request are at or below this carries simple-task traffic. Default 500.</summary>
        public int SimpleTaskMaxAvgTokens { get; set; } = 500;

        /// <summary>Minimum share of the month's AI spend a simple-task model must hold to be flagged as expensive. Default 0.30.</summary>
        public decimal ExpensiveModelMinCostShare { get; set; } = 0.30m;

        /// <summary>Minimum monthly requests a model needs before the expensive-model rule considers it. Default 50.</summary>
        public int ExpensiveModelMinRequests { get; set; } = 50;

        /// <summary>Assumed fraction of a flagged model's monthly spend saved by tier routing; cited as an assumption. Default 0.5.</summary>
        public decimal ModelTieringSavingRate { get; set; } = 0.5m;

        /// <summary>Cached-hit share below which the cache-miss rule fires. Default 0.10.</summary>
        public decimal MinCachedHitShare { get; set; } = 0.10m;

        /// <summary>Minimum monthly AI request volume before the cache-miss rule fires. Default 100.</summary>
        public int CacheMissMinRequests { get; set; } = 100;

        /// <summary>Month-over-month increase (percent) at or above which the cloud cost anomaly rule fires. Default 30.</summary>
        public decimal CloudCostAnomalyMinIncreasePercent { get; set; } = 30m;

        /// <summary>Minimum current-month spend (per provider and currency) before the anomaly rule fires. Default 100.</summary>
        public decimal CloudCostAnomalyMinMonthlySpend { get; set; } = 100m;

        /// <summary>Consumed share of the monthly AI request quota at or above which the quota rule fires. Default 0.8.</summary>
        public decimal QuotaApproachingMinUsedShare { get; set; } = 0.8m;

        /// <summary>Minimum monthly Licensing-category spend (per currency) before the licensing review fires. Default 100.</summary>
        public decimal LicensingReviewMinMonthlySpend { get; set; } = 100m;

        /// <summary>Default dismissal cooldown, in days, when a dismissal names none. Default 30.</summary>
        public int DismissCooldownDays { get; set; } = 30;

        public static AdvisorOptions From(IConfiguration? configuration)
        {
            var options = new AdvisorOptions();
            configuration?.GetSection(SectionName).Bind(options);
            return options;
        }
    }
}
