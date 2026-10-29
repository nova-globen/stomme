#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Metering;
using Stomme.Modules.Metering.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Metering.Persistence
{
    /// <summary>
    /// The shared delegation to the closed <see cref="IRepository{TAggregate, TId}"/>: no provider code, no
    /// <c>DbContext</c>, no RavenDB twin. Whatever store the install chose answers these calls.
    /// </summary>
    public abstract class MeteringRepositoryBase<TAggregate>(IRepository<TAggregate, Guid> inner)
        : IRepository<TAggregate, Guid>
        where TAggregate : AggregateRoot<Guid>
    {
        protected IRepository<TAggregate, Guid> Inner { get; } = inner;

        public Task<TAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => Inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<TAggregate>> GetAllAsync(CancellationToken ct = default) => Inner.GetAllAsync(ct);

        public Task AddAsync(TAggregate aggregate, CancellationToken ct = default) => Inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(TAggregate aggregate, CancellationToken ct = default) => Inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(TAggregate aggregate, CancellationToken ct = default) => Inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<TAggregate>> FindAsync(Expression<Func<TAggregate, bool>> predicate, CancellationToken ct = default) =>
            Inner.FindAsync(predicate, ct);

        /// <summary>Refuses a non-UTC instant rather than filtering against a local time the store stored as UTC.</summary>
        protected static DateTime UtcOnly(DateTime value) =>
            value.Kind == DateTimeKind.Utc
                ? value
                : throw new ArgumentException($"The instant must be a UTC DateTime (Kind was {value.Kind}).", nameof(value));
    }

    public sealed class QuotaRepository(IRepository<QuotaAggregate, Guid> inner)
        : MeteringRepositoryBase<QuotaAggregate>(inner), IQuotaRepository
    {
        public Task<IReadOnlyList<QuotaAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
            Inner.FindAsync(quota => quota.SiteId == siteId, ct);

        public async Task<QuotaAggregate?> GetForCurrentSiteAsync(Guid id, int siteId, CancellationToken ct = default)
        {
            var quota = await Inner.GetByIdAsync(id, ct);
            // The SAME answer for "no such quota" and "another site's quota": otherwise the id space is an oracle.
            return quota is null || quota.SiteId != siteId ? null : quota;
        }

        public async Task<QuotaAggregate?> GetAsync(TenantId tenantId, int siteId, string metric, QuotaPeriod period, CancellationToken ct = default)
        {
            var quota = await Inner.GetByIdAsync(QuotaIds.For(tenantId, siteId, MetricKeys.Normalize(metric), period), ct);
            return quota is null || quota.SiteId != siteId ? null : quota;
        }
    }

    public sealed class UsageRecordRepository(IRepository<UsageRecordAggregate, Guid> inner)
        : MeteringRepositoryBase<UsageRecordAggregate>(inner), IUsageRecordRepository
    {
        public Task<IReadOnlyList<UsageRecordAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
            Inner.FindAsync(row => row.SiteId == siteId, ct);

        public Task<IReadOnlyList<UsageRecordAggregate>> ListPendingAsync(int siteId, DateTime olderThanUtc, CancellationToken ct = default)
        {
            var cutoff = UtcOnly(olderThanUtc);
            return Inner.FindAsync(row => row.SiteId == siteId && row.FoldState == FoldState.Pending && row.OccurredOnUtc < cutoff, ct);
        }

        public Task<IReadOnlyList<UsageRecordAggregate>> ListForWindowAsync(int siteId, string metric, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        {
            // The metric key is canonical lowercase by construction, so it is its own normalized column; it is
            // normalized HERE too, with the same function, so both sides of the comparison agree.
            var key = MetricKeys.Normalize(metric);
            var from = UtcOnly(fromUtc);
            var to = UtcOnly(toUtc);
            return Inner.FindAsync(row => row.SiteId == siteId && row.Metric == key && row.OccurredOnUtc >= from && row.OccurredOnUtc < to, ct);
        }

        public Task<IReadOnlyList<UsageRecordAggregate>> ListSinceAsync(int siteId, DateTime fromUtc, CancellationToken ct = default)
        {
            var from = UtcOnly(fromUtc);
            return Inner.FindAsync(row => row.SiteId == siteId && row.OccurredOnUtc >= from, ct);
        }

        public Task<IReadOnlyList<UsageRecordAggregate>> ListOlderThanAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default)
        {
            var cutoff = UtcOnly(cutoffUtc);
            return Inner.FindAsync(row => row.SiteId == siteId && row.OccurredOnUtc < cutoff, ct);
        }

        public Task<IReadOnlyList<UsageRecordAggregate>> ListByUserAsync(int siteId, int userId, CancellationToken ct = default) =>
            Inner.FindAsync(row => row.SiteId == siteId && row.UserId == userId, ct);
    }

    public sealed class AiUsageRecordRepository(IRepository<AiUsageRecordAggregate, Guid> inner)
        : MeteringRepositoryBase<AiUsageRecordAggregate>(inner), IAiUsageRecordRepository
    {
        public Task<IReadOnlyList<AiUsageRecordAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
            Inner.FindAsync(row => row.SiteId == siteId, ct);

        public Task<IReadOnlyList<AiUsageRecordAggregate>> ListSinceAsync(int siteId, DateTime fromUtc, CancellationToken ct = default)
        {
            var from = UtcOnly(fromUtc);
            return Inner.FindAsync(row => row.SiteId == siteId && row.OccurredOnUtc >= from, ct);
        }

        public Task<IReadOnlyList<AiUsageRecordAggregate>> ListOlderThanAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default)
        {
            var cutoff = UtcOnly(cutoffUtc);
            return Inner.FindAsync(row => row.SiteId == siteId && row.OccurredOnUtc < cutoff, ct);
        }

        public Task<IReadOnlyList<AiUsageRecordAggregate>> ListByUserAsync(int siteId, int userId, CancellationToken ct = default) =>
            Inner.FindAsync(row => row.SiteId == siteId && row.UserId == userId, ct);
    }

    public sealed class AiUsageRollupRepository(IRepository<AiUsageRollupAggregate, Guid> inner)
        : MeteringRepositoryBase<AiUsageRollupAggregate>(inner), IAiUsageRollupRepository
    {
        public Task<IReadOnlyList<AiUsageRollupAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
            Inner.FindAsync(row => row.SiteId == siteId, ct);

        public Task<IReadOnlyList<AiUsageRollupAggregate>> ListSinceAsync(int siteId, DateTime fromDayUtc, CancellationToken ct = default)
        {
            var from = UtcOnly(fromDayUtc);
            return Inner.FindAsync(row => row.SiteId == siteId && row.DayUtc >= from, ct);
        }

        public Task<IReadOnlyList<AiUsageRollupAggregate>> ListOlderThanAsync(int siteId, DateTime cutoffDayUtc, CancellationToken ct = default)
        {
            var cutoff = UtcOnly(cutoffDayUtc);
            return Inner.FindAsync(row => row.SiteId == siteId && row.DayUtc < cutoff, ct);
        }
    }
}
