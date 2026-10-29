#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Dashboards.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Dashboards.Persistence
{
    /// <summary>The Dashboards module's closed repository over the storage-blind store: a site's boards by normalized name, and all of a site's boards in name order.</summary>
    public interface IDashboardRepository : IRepository<DashboardAggregate, Guid>
    {
        Task<IReadOnlyList<DashboardAggregate>> ListForSiteAsync(int siteId, CancellationToken ct = default);

        Task<DashboardAggregate?> FindByNameAsync(int siteId, string name, CancellationToken ct = default);
    }

    public sealed class DashboardRepository(IRepository<DashboardAggregate, Guid> inner) : IDashboardRepository
    {
        public Task<DashboardAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<DashboardAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync(DashboardAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(DashboardAggregate aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(DashboardAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<DashboardAggregate>> FindAsync(Expression<Func<DashboardAggregate, bool>> predicate, CancellationToken ct = default) => inner.FindAsync(predicate, ct);

        public async Task<IReadOnlyList<DashboardAggregate>> ListForSiteAsync(int siteId, CancellationToken ct = default) =>
            (await inner.FindAsync(dashboard => dashboard.SiteId == siteId, ct)).OrderBy(dashboard => dashboard.NameNormalized, StringComparer.Ordinal).ToList();

        public async Task<DashboardAggregate?> FindByNameAsync(int siteId, string name, CancellationToken ct = default)
        {
            var normalized = DashboardAggregate.NormalizeName(name);
            return (await inner.FindAsync(dashboard => dashboard.SiteId == siteId && dashboard.NameNormalized == normalized, ct)).FirstOrDefault();
        }
    }
}
