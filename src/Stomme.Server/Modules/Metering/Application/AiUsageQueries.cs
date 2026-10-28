#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Models.Metering;
using Stomme.Metering.Persistence;
using Stomme.Security.Abstractions;
using Stomme.Shared;

namespace Stomme.Modules.Metering.Application
{
    /// <summary>The site's AI dashboard, from the rollups the nightly pass maintains - never a ledger scan.</summary>
    [Requires(global::Stomme.Metering.MeteringPermissions.View)]
    public sealed record GetAiUsageDashboardQuery(int Days = 30) : IQuery<AiUsageDashboard>;

    public sealed class GetAiUsageDashboardQueryHandler(IAiUsageRollupRepository rollups, SiteState siteState, IMeteringClock clock)
        : IQueryHandler<GetAiUsageDashboardQuery, AiUsageDashboard>
    {
        public async Task<AiUsageDashboard> Handle(GetAiUsageDashboardQuery request, CancellationToken ct)
        {
            var siteId = MeteringWire.RequireSiteId(siteState);
            var days = Math.Clamp(request.Days, 1, 730);
            var nowUtc = clock.UtcNow;
            var from = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-days);

            var rows = (await rollups.ListSinceAsync(siteId, from, ct))
                .OrderByDescending(row => row.DayUtc)
                .ThenBy(row => row.ProviderNormalized)
                .ToList();

            return new AiUsageDashboard
            {
                Rollups = rows.Select(MeteringWire.ToView).ToList(),
                Calls = rows.Sum(row => row.Calls),
                BillableTokens = rows.Sum(row => row.BillableTokens()),
                Errors = rows.Sum(row => row.Errors),
                EstimatedCost = rows.Sum(row => row.EstimatedCost),
                CurrencyCode = rows.Select(row => row.CurrencyCode).FirstOrDefault() ?? "USD"
            };
        }
    }

    /// <summary>The site's individual AI calls, most recent first. Never a prompt or a completion - the ledger holds neither.</summary>
    [Requires(global::Stomme.Metering.MeteringPermissions.View)]
    public sealed record ListAiUsageQuery(int Take = 200) : IQuery<IReadOnlyList<AiUsageRecordView>>;

    public sealed class ListAiUsageQueryHandler(IAiUsageRecordRepository aiUsage, SiteState siteState)
        : IQueryHandler<ListAiUsageQuery, IReadOnlyList<AiUsageRecordView>>
    {
        public async Task<IReadOnlyList<AiUsageRecordView>> Handle(ListAiUsageQuery request, CancellationToken ct) =>
            (await aiUsage.ListBySiteAsync(MeteringWire.RequireSiteId(siteState), ct))
                .OrderByDescending(row => row.OccurredOnUtc)
                .Take(Math.Clamp(request.Take, 1, 1000))
                .Select(MeteringWire.ToView)
                .ToList();
    }
}
