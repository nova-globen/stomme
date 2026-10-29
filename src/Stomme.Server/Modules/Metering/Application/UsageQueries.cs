#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Models.Metering;
using Stomme.Modules.Metering.Domain;
using Stomme.Metering.Persistence;
using Stomme.Security.Abstractions;
using Stomme.Shared;

namespace Stomme.Modules.Metering.Application
{
    /// <summary>The site's quota definitions and their live counters.</summary>
    [Requires(global::Stomme.Metering.MeteringPermissions.View)]
    public sealed record ListQuotasQuery : IQuery<IReadOnlyList<QuotaView>>;

    public sealed class ListQuotasQueryHandler(IQuotaRepository quotas, SiteState siteState, IMeteringClock clock)
        : IQueryHandler<ListQuotasQuery, IReadOnlyList<QuotaView>>
    {
        public async Task<IReadOnlyList<QuotaView>> Handle(ListQuotasQuery request, CancellationToken ct) =>
            (await quotas.ListBySiteAsync(MeteringWire.RequireSiteId(siteState), ct))
                .OrderBy(quota => quota.Metric).ThenBy(quota => quota.Period)
                .Select(quota => MeteringWire.ToView(quota, clock.UtcNow))
                .ToList();
    }

    /// <summary>One quota of THIS site, or null - the same answer a caller naming another site's quota gets.</summary>
    [Requires(global::Stomme.Metering.MeteringPermissions.View)]
    public sealed record GetQuotaQuery(Guid Id) : IQuery<QuotaView?>;

    public sealed class GetQuotaQueryHandler(IQuotaRepository quotas, SiteState siteState, IMeteringClock clock)
        : IQueryHandler<GetQuotaQuery, QuotaView?>
    {
        public async Task<QuotaView?> Handle(GetQuotaQuery request, CancellationToken ct)
        {
            var quota = await quotas.GetForCurrentSiteAsync(request.Id, MeteringWire.RequireSiteId(siteState), ct);
            return quota is null ? null : MeteringWire.ToView(quota, clock.UtcNow);
        }
    }

    /// <summary>The site's usage ledger, most recent first, bounded by the retention the site keeps.</summary>
    [Requires(global::Stomme.Metering.MeteringPermissions.View)]
    public sealed record ListUsageQuery(string? Metric = null, int Take = 200) : IQuery<IReadOnlyList<UsageRecordView>>;

    public sealed class ListUsageQueryHandler(IUsageRecordRepository usage, SiteState siteState)
        : IQueryHandler<ListUsageQuery, IReadOnlyList<UsageRecordView>>
    {
        public async Task<IReadOnlyList<UsageRecordView>> Handle(ListUsageQuery request, CancellationToken ct)
        {
            var siteId = MeteringWire.RequireSiteId(siteState);
            var rows = await usage.ListBySiteAsync(siteId, ct);
            var metric = string.IsNullOrWhiteSpace(request.Metric) ? null : global::Stomme.Metering.MetricKeys.Normalize(request.Metric);
            return rows
                .Where(row => metric is null || row.Metric == metric)
                .OrderByDescending(row => row.OccurredOnUtc)
                .Take(Math.Clamp(request.Take, 1, 1000))
                .Select(MeteringWire.ToView)
                .ToList();
        }
    }

    /// <summary>
    /// The CALLER's own consumption on this site - one line per metric, against the quota that governs it.
    /// </summary>
    /// <remarks>
    /// <b>The query takes no user id</b>: the subject is <c>ICurrentUser</c>, so a caller cannot ask
    /// for someone else's line by changing a parameter. An anonymous caller has no consumption to report - though
    /// <c>[Requires(ViewOwn)]</c> denies it before this handler runs.
    /// </remarks>
    [Requires(global::Stomme.Metering.MeteringPermissions.ViewOwn)]
    public sealed record GetMyUsageQuery : IQuery<IReadOnlyList<MyUsageLine>>;

    public sealed class GetMyUsageQueryHandler(
        IUsageRecordRepository usage,
        IQuotaRepository quotas,
        ICurrentUser currentUser,
        SiteState siteState,
        IMeteringClock clock)
        : IQueryHandler<GetMyUsageQuery, IReadOnlyList<MyUsageLine>>
    {
        public async Task<IReadOnlyList<MyUsageLine>> Handle(GetMyUsageQuery request, CancellationToken ct)
        {
            if (currentUser.UserId is not { } userId || userId <= 0)
            {
                return Array.Empty<MyUsageLine>();
            }

            var siteId = MeteringWire.RequireSiteId(siteState);
            var nowUtc = clock.UtcNow;
            var siteQuotas = await quotas.ListBySiteAsync(siteId, ct);
            var mine = await usage.ListByUserAsync(siteId, userId, ct);

            var lines = new List<MyUsageLine>();
            var covered = new HashSet<string>(StringComparer.Ordinal);

            foreach (var quota in siteQuotas.OrderBy(quota => quota.Metric))
            {
                var windowStart = QuotaAggregate.WindowStart(quota.Period, nowUtc);
                var consumed = mine
                    .Where(row => row.Metric == quota.Metric && row.OccurredOnUtc >= windowStart)
                    .Sum(row => row.Quantity);

                covered.Add(quota.Metric);
                lines.Add(new MyUsageLine
                {
                    Metric = quota.Metric,
                    MyConsumption = consumed,
                    SiteConsumption = quota.Consumed,
                    Limit = quota.IsUnlimited() ? null : quota.EffectiveLimit(nowUtc),
                    Unlimited = quota.IsUnlimited(),
                    Period = quota.Period.ToString()
                });
            }

            // Metrics the caller consumed that no quota governs: reported as totals, never silently dropped.
            foreach (var group in mine.Where(row => !covered.Contains(row.Metric)).GroupBy(row => row.Metric).OrderBy(group => group.Key))
            {
                lines.Add(new MyUsageLine
                {
                    Metric = group.Key,
                    MyConsumption = group.Sum(row => row.Quantity),
                    SiteConsumption = null,
                    Limit = null,
                    Unlimited = false,
                    Period = QuotaPeriod.Total.ToString()
                });
            }

            return lines;
        }
    }
}
