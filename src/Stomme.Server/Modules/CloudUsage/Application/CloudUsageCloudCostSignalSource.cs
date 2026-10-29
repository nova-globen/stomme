#nullable enable
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CloudUsage.Persistence;
using Stomme.CostOptimization;
using Stomme.Modules.CloudUsage.Domain;
using Stomme.Shared;

namespace Stomme.Modules.CloudUsage.Application
{
    /// <summary>
    /// CloudUsage's side of CostOptimization's cloud-cost read port, owned by the module that holds the ledger: the request's SITE's rows for
    /// this calendar month and the one before - per provider and currency with last month as the baseline, and this month's
    /// per-service breakdown. Per currency, never summed across currencies, as every CloudUsage rollup.
    /// </summary>
    public sealed class CloudUsageCloudCostSignalSource(ICloudCostRecordRepository records, SiteState siteState, TimeProvider clock) : ICloudCostSignalSource
    {
        public async Task<CloudCostSignal> GetCurrentMonthSpendAsync(CancellationToken ct = default)
        {
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var monthStart = new DateOnly(today.Year, today.Month, 1);
            var previousStart = monthStart.AddMonths(-1);
            var current = CloudCostRecordAggregate.ToStoredDate(monthStart);
            var window = siteState.Alias?.SiteId is { } siteId
                ? await records.ListForSiteAsync(siteId, CloudCostRecordAggregate.ToStoredDate(previousStart), CloudCostRecordAggregate.ToStoredDate(monthStart.AddMonths(1)), ct)
                : Array.Empty<CloudCostRecordAggregate>();

            var byProvider = window
                .GroupBy(r => new { r.Provider, r.Currency })
                .OrderBy(g => g.Key.Provider)
                .ThenBy(g => g.Key.Currency, StringComparer.Ordinal)
                .Select(g => new CloudSpendSignal(g.Key.Provider.ToString(), g.Key.Currency, g.Where(r => r.DateUtc >= current).Sum(r => r.Cost), g.Where(r => r.DateUtc < current).Sum(r => r.Cost)))
                .ToList();
            var byService = window
                .Where(r => r.DateUtc >= current)
                .GroupBy(r => new { r.ServiceCategory, r.Service, r.Currency })
                .OrderByDescending(g => g.Sum(r => r.Cost))
                .ThenBy(g => g.Key.Service, StringComparer.OrdinalIgnoreCase)
                .Select(g => new CloudServiceSpendSignal(g.Key.ServiceCategory.ToString(), g.Key.Service, g.Key.Currency, g.Sum(r => r.Cost)))
                .ToList();
            return new CloudCostSignal(monthStart.ToString("yyyy-MM", CultureInfo.InvariantCulture), previousStart.ToString("yyyy-MM", CultureInfo.InvariantCulture), byProvider, byService);
        }
    }
}
