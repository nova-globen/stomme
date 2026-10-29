#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Stomme.Metering;
using Stomme.Metering.Persistence;
using Stomme.Modules.Metering.Domain;

namespace Stomme.Modules.Metering.Application
{
    /// <summary>
    /// Metering's side of CostOptimization's AI-usage read port: the REQUEST's site's AI ledger for the
    /// current calendar month per provider and model - requests, cache-served requests, raw tokens, cost - and the site's
    /// monthly <c>ai.calls</c> quota position. With no site resolved the signal is empty, never another site's.
    /// </summary>
    /// <remarks>
    /// <b>Currency:</b> the price list names a currency per model, so
    /// a month can hold two. The signal carries one currency (the port's shape, which the analyzers compare shares within),
    /// so it is the month's DOMINANT priced currency by row count - USD when nothing is priced - and a model's cost counts
    /// only its rows in that currency; requests, cache hits and tokens count every row. No FX, as everywhere in this repo.
    /// The quota is the site's monthly <c>ai.calls</c> quota at its effective limit (a granted override included); an
    /// unlimited quota, or none, carries no quota signal (an "unlimited tenant").
    /// </remarks>
    public sealed class MeteringAssistantUsageSignalSource(
        IAiUsageRecordRepository aiUsage,
        IQuotaRepository quotas,
        MeteringScope scope,
        IMeteringClock clock) : IAssistantUsageSignalSource
    {
        public async Task<AssistantUsageSignal> GetCurrentMonthUsageAsync(CancellationToken ct = default)
        {
            var now = clock.UtcNow;
            var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var month = monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            var (siteId, tenantId, _) = await scope.ResolveAsync();
            if (siteId <= 0 || tenantId is null)
            {
                return new AssistantUsageSignal(month, AiCostCalculator.DefaultCurrency, Array.Empty<AssistantModelUsageSignal>(), null);
            }

            var next = monthStart.AddMonths(1);
            var rows = (await aiUsage.ListSinceAsync(siteId, monthStart, ct)).Where(row => row.SiteId == siteId && row.OccurredOnUtc < next).ToList();
            var currency = rows
                .Where(row => row.EstimatedCost != 0m && !string.IsNullOrEmpty(row.CurrencyCode))
                .GroupBy(row => row.CurrencyCode, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key)
                .FirstOrDefault() ?? AiCostCalculator.DefaultCurrency;

            var byModel = rows
                .GroupBy(row => new { row.Provider, row.Model })
                .OrderBy(group => group.Key.Provider, StringComparer.OrdinalIgnoreCase)
                .ThenBy(group => group.Key.Model, StringComparer.OrdinalIgnoreCase)
                .Select(group => new AssistantModelUsageSignal(
                    group.Key.Provider,
                    group.Key.Model,
                    group.LongCount(),
                    group.LongCount(row => row.Status == AiCallStatus.Cached),
                    group.Sum(row => row.InputTokens + row.OutputTokens),
                    group.Where(row => string.Equals(row.CurrencyCode, currency, StringComparison.Ordinal)).Sum(row => row.EstimatedCost)))
                .ToList();

            var quota = await quotas.GetAsync(tenantId, siteId, AiUsageRecorder.CallsMetric, QuotaPeriod.Monthly, ct);
            var quotaSignal = quota is null || quota.IsUnlimited() ? null : new AssistantQuotaSignal(quota.EffectiveLimit(now), quota.Consumed);
            return new AssistantUsageSignal(month, currency, byModel, quotaSignal);
        }
    }
}
