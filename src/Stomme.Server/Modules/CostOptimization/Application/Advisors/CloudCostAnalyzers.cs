#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Stomme.Modules.CostOptimization.Domain;

// The thresholds arrive as the host-read AdvisorOptions rather than IOptions, and a signal source reads the
// REQUEST's site - so a draft is a site's.
namespace Stomme.Modules.CostOptimization.Application.Advisors
{
    /// <summary>
    /// Flags a cost source whose spend in one currency rose month-over-month by at least the configured
    /// percentage (over a minimum spend floor, so tiny bases do not alarm) — the Cloud Usage (36)
    /// month-over-month signal turned into an actionable right-sizing review. The estimated saving is
    /// the increase itself: what reverting to last month's footprint would recover.
    /// </summary>
    public sealed class CloudCostAnomalyAnalyzer(
        AdvisorOptions options,
        ICloudCostSignalSource? costSignals = null) : IRecommendationAnalyzer
    {
        /// <inheritdoc />
        public async Task<IReadOnlyList<RecommendationDraft>> AnalyzeAsync(CancellationToken ct = default)
        {
            if (costSignals is null)
            {
                return [];
            }

            var thresholds = options;
            var spend = await costSignals.GetCurrentMonthSpendAsync(ct);
            var drafts = new List<RecommendationDraft>();
            foreach (var line in spend.ByProvider)
            {
                if (line.PreviousMonthTotal <= 0m || line.CurrentMonthTotal < thresholds.CloudCostAnomalyMinMonthlySpend)
                {
                    continue;
                }

                var delta = line.CurrentMonthTotal - line.PreviousMonthTotal;
                var increasePercent = delta / line.PreviousMonthTotal * 100m;
                if (increasePercent < thresholds.CloudCostAnomalyMinIncreasePercent)
                {
                    continue;
                }

                drafts.Add(new RecommendationDraft(
                    RecommendationCategory.RightSizing,
                    Invariant($"right-sizing:cloud-cost:{line.Provider.ToLowerInvariant()}:{line.Currency.ToLowerInvariant()}:mom-increase"),
                    Invariant($"Review {line.Provider} spend: it rose {decimal.Round(increasePercent, 1)}% month-over-month."),
                    [
                        Invariant($"{spend.Month}: {line.Provider} spend was {line.CurrentMonthTotal} {line.Currency}, up from {line.PreviousMonthTotal} {line.Currency} in {spend.PreviousMonth} (+{decimal.Round(increasePercent, 1)}%; alert threshold: {thresholds.CloudCostAnomalyMinIncreasePercent}%)."),
                        Invariant($"Estimated saving is the month-over-month increase of {decimal.Round(delta, 2)} {line.Currency} — what reverting to the previous month's footprint would recover."),
                    ],
                    EstimatedSaving.Of(decimal.Round(delta, 2), line.Currency)));
            }

            return drafts;
        }

        private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Flags a month whose Licensing-category SaaS spend (per currency) reached the configured floor and
    /// recommends a seat-utilization review, citing the licensed services and what each cost. Seat-level
    /// utilization is not a published signal yet, so the draft carries a zero saving — it names the
    /// concrete spend to review rather than fabricating a waste estimate.
    /// </summary>
    public sealed class LicensingSpendReviewAnalyzer(
        AdvisorOptions options,
        ICloudCostSignalSource? costSignals = null) : IRecommendationAnalyzer
    {
        /// <summary>The normalized Unified-Cost-Schema category name this analyzer selects.</summary>
        public const string LicensingCategory = "Licensing";

        /// <inheritdoc />
        public async Task<IReadOnlyList<RecommendationDraft>> AnalyzeAsync(CancellationToken ct = default)
        {
            if (costSignals is null)
            {
                return [];
            }

            var thresholds = options;
            var spend = await costSignals.GetCurrentMonthSpendAsync(ct);
            var drafts = new List<RecommendationDraft>();
            var licensingByCurrency = spend.ByService
                .Where(s => string.Equals(s.ServiceCategory, LicensingCategory, StringComparison.OrdinalIgnoreCase))
                .GroupBy(s => s.Currency, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var group in licensingByCurrency)
            {
                var total = group.Sum(s => s.CurrentMonthTotal);
                if (total < thresholds.LicensingReviewMinMonthlySpend)
                {
                    continue;
                }

                var serviceLines = group
                    .OrderByDescending(s => s.CurrentMonthTotal)
                    .Select(s => Invariant($"{spend.Month}: {s.Service} licensing cost {s.CurrentMonthTotal} {s.Currency}."));
                drafts.Add(new RecommendationDraft(
                    RecommendationCategory.RightSizing,
                    Invariant($"right-sizing:licensing:{group.Key.ToLowerInvariant()}:seat-review"),
                    Invariant($"Review license seat utilization: {total} {group.Key} of licensing spend this month."),
                    [
                        Invariant($"{spend.Month}: Licensing-category spend totaled {total} {group.Key} (review floor: {thresholds.LicensingReviewMinMonthlySpend} {group.Key})."),
                        .. serviceLines,
                        "Seat-level utilization is not connected, so no saving is estimated — audit unused seats against these invoices.",
                    ],
                    EstimatedSaving.Of(0m, group.Key)));
            }

            return drafts;
        }

        private static string Invariant(FormattableString value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
