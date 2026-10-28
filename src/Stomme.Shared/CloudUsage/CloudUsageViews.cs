#nullable enable
using System;
using System.Collections.Generic;

namespace Stomme.CloudUsage
{
    // ============================================================================================================
    // The CloudUsage views and requests (BL-97 increment 4): what the page and the API read and send. Totals are per
    // ISO 4217 currency - costs in different currencies are never summed together (no FX, as the predecessor platform).
    // ============================================================================================================

    public sealed record CloudCostCurrencyTotal(string Currency, decimal TotalCost, long RecordCount);

    public sealed record CloudCostSummaryView(DateOnly? From, DateOnly? To, long RecordCount, IReadOnlyList<CloudCostCurrencyTotal> TotalsByCurrency);

    public sealed record CloudCostProviderBreakdown(CloudProvider Provider, string Currency, decimal TotalCost, long RecordCount);

    public sealed record CloudCostByProviderView(DateOnly? From, DateOnly? To, IReadOnlyList<CloudCostProviderBreakdown> ByProvider);

    public sealed record CloudCostServiceBreakdown(CloudServiceCategory ServiceCategory, string Service, string Currency, decimal TotalCost, long RecordCount);

    public sealed record CloudCostByServiceView(DateOnly? From, DateOnly? To, IReadOnlyList<CloudCostServiceBreakdown> ByService);

    public sealed record CloudCostMonthDelta(string Currency, decimal CurrentTotal, decimal PreviousTotal, decimal Delta, decimal? DeltaPercent);

    public sealed record CloudCostMonthOverMonthView(string Month, string PreviousMonth, IReadOnlyList<CloudCostMonthDelta> ByCurrency);

    public sealed record CloudBudgetStatusLine(Guid BudgetId, CloudProvider? Provider, string Currency, decimal BudgetAmount, decimal ActualSpend, decimal RemainingAmount, decimal PercentUsed);

    public sealed record CloudBudgetStatusView(string Month, IReadOnlyList<CloudBudgetStatusLine> Budgets);

    public sealed record CloudBudgetView(Guid BudgetId, CloudProvider? Provider, decimal MonthlyAmount, string Currency);

    /// <summary>What a sync did: the window, the rows it recorded, the dataset it refreshed - or the refusal's code and reason.</summary>
    public sealed record CloudCostSyncView(bool Success, string? Code, string Reason, CloudProvider? Provider, DateOnly? From, DateOnly? To, int SyncedRows, string? Dataset);

    public sealed class SyncCloudCostsRequest
    {
        public Guid ConnectionId { get; set; }

        /// <summary>The cost source by name (<c>azure</c>, <c>aws</c>, <c>datadog</c> ...).</summary>
        public string Provider { get; set; } = string.Empty;

        /// <summary><c>daily</c> (yesterday, the default) or <c>monthly</c> (last month).</summary>
        public string? Window { get; set; }

        /// <summary>A dataset to register or refresh with the rows; none materialises nothing.</summary>
        public string? TargetDataset { get; set; }

        /// <summary><c>run</c> (this sync's rows, the default) or <c>ledger</c> (every row the site holds).</summary>
        public string? DatasetScope { get; set; }
    }

    public sealed class SetCloudBudgetRequest
    {
        public decimal MonthlyAmount { get; set; }

        public string Currency { get; set; } = "USD";

        /// <summary>Null for every provider; one provider otherwise. One budget per scope - a second set amends it.</summary>
        public CloudProvider? Provider { get; set; }
    }
}
