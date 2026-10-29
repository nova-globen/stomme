#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.CostOptimization
{
    // ============================================================================================================
    // CostOptimization's READ ports: what the advisors read about the
    // site's spend, published here so the module that HOLDS the data implements them and CostOptimization never reaches
    // into another module's repository. BCL-only like every Shared contract. Every figure is per currency - never summed
    // across currencies, never converted.
    // ============================================================================================================

    /// <summary>One provider's spend in one currency: this calendar month so far, and the whole previous month.</summary>
    public sealed record CloudSpendSignal(string Provider, string Currency, decimal CurrentMonthTotal, decimal PreviousMonthTotal);

    /// <summary>One service's spend this month, in one currency.</summary>
    public sealed record CloudServiceSpendSignal(string ServiceCategory, string Service, string Currency, decimal CurrentMonthTotal);

    public sealed record CloudCostSignal(string Month, string PreviousMonth, IReadOnlyList<CloudSpendSignal> ByProvider, IReadOnlyList<CloudServiceSpendSignal> ByService);

    /// <summary>The request's site's cloud spend this month and last (implemented by CloudUsage).</summary>
    public interface ICloudCostSignalSource
    {
        Task<CloudCostSignal> GetCurrentMonthSpendAsync(CancellationToken ct = default);
    }

    /// <summary>One model's calls this month: how many, how many answered from cache, the tokens and the estimated cost.</summary>
    public sealed record AssistantModelUsageSignal(string Provider, string Model, long RequestCount, long CachedRequestCount, long TotalTokens, decimal EstimatedCost);

    public sealed record AssistantQuotaSignal(long Limit, long Consumed);

    public sealed record AssistantUsageSignal(string Month, string Currency, IReadOnlyList<AssistantModelUsageSignal> ByModel, AssistantQuotaSignal? Quota);

    /// <summary>The request's site's AI usage this month (implemented by Metering).</summary>
    public interface IAssistantUsageSignalSource
    {
        Task<AssistantUsageSignal> GetCurrentMonthUsageAsync(CancellationToken ct = default);
    }
}
