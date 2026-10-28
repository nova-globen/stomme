#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Health.Persistence;
using Stomme.Modules.Health.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Tests
{
    /// <summary>
    /// The in-memory doubles the health suites share.
    /// </summary>
    /// <remarks>
    /// Shared rather than copied per suite: a duplicated test double drifts, and then two suites quietly disagree
    /// about what the repository does. The real repositories are thin delegations over the generic
    /// <c>IRepository</c>, so an in-memory dictionary is a faithful stand-in for everything EXCEPT provider
    /// divergence — which is why the store-level behaviour is a separate, fail-not-skip job for the conformance
    /// suites rather than something these can speak to.
    /// </remarks>
    internal sealed class HealthNoOpUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken ct = default) => Task.FromResult(0);

        public void Dispose()
        {
        }
    }

    internal sealed class InMemoryHealthRollups : IHealthRollupRepository
    {
        public Dictionary<Guid, HealthRollupAggregate> Rows { get; } = new();

        public Task<HealthRollupAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Rows.TryGetValue(id, out var row) ? row : null);

        public Task<IReadOnlyList<HealthRollupAggregate>> GetAllAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HealthRollupAggregate>>(Rows.Values.ToList());

        public Task AddAsync(HealthRollupAggregate aggregate, CancellationToken ct = default)
        {
            Rows[aggregate.Id] = aggregate;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(HealthRollupAggregate aggregate, CancellationToken ct = default)
        {
            Rows[aggregate.Id] = aggregate;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(HealthRollupAggregate aggregate, CancellationToken ct = default)
        {
            Rows.Remove(aggregate.Id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<HealthRollupAggregate>> FindAsync(Expression<Func<HealthRollupAggregate, bool>> predicate, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<HealthRollupAggregate>>(Rows.Values.Where(predicate.Compile()).ToList());

        public async Task<IReadOnlyList<HealthRollupAggregate>> ListAsync(HealthProbeScope scope, int siteId, HealthRollupWindow window, CancellationToken ct = default) =>
            (await FindAsync(row => row.SiteId == siteId && row.Scope == scope && row.Window == window, ct))
                .OrderByDescending(row => row.PeriodStartUtc).ToList();

        public async Task<IReadOnlyList<HealthRollupAggregate>> ListForRangeAsync(HealthProbeScope scope, int siteId, HealthRollupWindow window, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) =>
            (await FindAsync(row => row.SiteId == siteId && row.Scope == scope && row.Window == window && row.PeriodStartUtc >= fromUtc && row.PeriodStartUtc < toUtc, ct))
                .OrderBy(row => row.PeriodStartUtc).ToList();

        public Task<IReadOnlyList<HealthRollupAggregate>> ListOlderThanAsync(HealthProbeScope scope, int siteId, HealthRollupWindow window, DateTime cutoffUtc, CancellationToken ct = default) =>
            FindAsync(row => row.SiteId == siteId && row.Scope == scope && row.Window == window && row.PeriodStartUtc < cutoffUtc, ct);

        public Task<IReadOnlyList<HealthRollupAggregate>> ListOlderThanAnywhereAsync(int siteId, HealthRollupWindow window, DateTime cutoffUtc, CancellationToken ct = default) =>
            FindAsync(row => row.SiteId == siteId && row.Window == window && row.PeriodStartUtc < cutoffUtc, ct);
    }
}
