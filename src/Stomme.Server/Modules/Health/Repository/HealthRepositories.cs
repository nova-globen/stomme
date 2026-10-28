#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Modules.Health.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Health.Persistence
{
    /// <summary>
    /// The shared delegation to the closed <see cref="IRepository{TAggregate, TId}"/>: no provider code, no
    /// <c>DbContext</c>, no RavenDB twin. Whatever store the install chose answers these calls (D79/D80).
    /// </summary>
    public abstract class HealthRepositoryBase<TAggregate>(IRepository<TAggregate, Guid> inner)
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

        /// <summary>
        /// Refuses a non-UTC instant rather than filtering against a local time the store stored as UTC. The same
        /// guard the metering repositories carry, for the same measured reason (D102).
        /// </summary>
        protected static DateTime UtcOnly(DateTime value) =>
            value.Kind == DateTimeKind.Utc
                ? value
                : throw new ArgumentException($"The instant must be a UTC DateTime (Kind was {value.Kind}).", nameof(value));
    }

    public sealed class HealthRollupRepository(IRepository<HealthRollupAggregate, Guid> inner)
        : HealthRepositoryBase<HealthRollupAggregate>(inner), IHealthRollupRepository
    {
        public async Task<IReadOnlyList<HealthRollupAggregate>> ListAsync(
            HealthProbeScope scope, int siteId, HealthRollupWindow window, CancellationToken ct = default)
        {
            var rows = await Inner.FindAsync(row => row.SiteId == siteId && row.Scope == scope && row.Window == window, ct);
            return rows.OrderByDescending(row => row.PeriodStartUtc).ToList();
        }

        public async Task<IReadOnlyList<HealthRollupAggregate>> ListForRangeAsync(
            HealthProbeScope scope, int siteId, HealthRollupWindow window, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        {
            var from = UtcOnly(fromUtc);
            var to = UtcOnly(toUtc);
            var rows = await Inner.FindAsync(
                row => row.SiteId == siteId && row.Scope == scope && row.Window == window
                    && row.PeriodStartUtc >= from && row.PeriodStartUtc < to, ct);
            return rows.OrderBy(row => row.PeriodStartUtc).ToList();
        }

        public Task<IReadOnlyList<HealthRollupAggregate>> ListOlderThanAsync(
            HealthProbeScope scope, int siteId, HealthRollupWindow window, DateTime cutoffUtc, CancellationToken ct = default)
        {
            var cutoff = UtcOnly(cutoffUtc);
            return Inner.FindAsync(
                row => row.SiteId == siteId && row.Scope == scope && row.Window == window && row.PeriodStartUtc < cutoff, ct);
        }

        public Task<IReadOnlyList<HealthRollupAggregate>> ListOlderThanAnywhereAsync(
            int siteId, HealthRollupWindow window, DateTime cutoffUtc, CancellationToken ct = default)
        {
            var cutoff = UtcOnly(cutoffUtc);
            return Inner.FindAsync(row => row.SiteId == siteId && row.Window == window && row.PeriodStartUtc < cutoff, ct);
        }
    }

    public sealed class HealthAlertStateRepository(IRepository<HealthAlertStateAggregate, Guid> inner)
        : HealthRepositoryBase<HealthAlertStateAggregate>(inner), IHealthAlertStateRepository
    {
        public Task<IReadOnlyList<HealthAlertStateAggregate>> ListBySiteAsync(int siteId, CancellationToken ct = default) =>
            Inner.FindAsync(row => row.SiteId == siteId, ct);

        public async Task<HealthAlertStateAggregate?> GetAsync(
            TenantId tenantId, HealthProbeScope scope, int siteId, HealthSubject subject, CancellationToken ct = default)
        {
            // Addressed by id rather than filtered by subject: see the remarks on IHealthAlertStateRepository.
            var row = await Inner.GetByIdAsync(HealthIds.ForAlertState(tenantId, scope, siteId, subject), ct);
            return row is null || row.SiteId != siteId || row.Scope != scope ? null : row;
        }

        public async Task<HealthAlertStateAggregate?> GetForCurrentSiteAsync(Guid id, int siteId, CancellationToken ct = default)
        {
            var row = await Inner.GetByIdAsync(id, ct);
            // The SAME answer for "no such row" and "another site's row": otherwise the id space is an oracle.
            return row is null || row.SiteId != siteId ? null : row;
        }
    }
}
