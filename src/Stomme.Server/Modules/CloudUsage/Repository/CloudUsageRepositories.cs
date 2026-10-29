#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CloudUsage;
using Stomme.Modules.CloudUsage.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.CloudUsage.Persistence
{
    /// <summary>
    /// The cost rows' closed repository over the storage-blind store. Every read is a SITE's; a window is a
    /// range on the stored UTC-midnight date (<c>from</c> inclusive, <c>to</c> exclusive) - equality and range on non-string
    /// scalars are the FindAsync contract's safe set, so the three providers answer alike.
    /// </summary>
    public interface ICloudCostRecordRepository : IRepository<CloudCostRecordAggregate, Guid>
    {
        Task<IReadOnlyList<CloudCostRecordAggregate>> ListForSiteAsync(int siteId, DateTime? fromUtc = null, DateTime? toUtcExclusive = null, CancellationToken ct = default);
    }

    /// <summary>The budgets' closed repository: a site's budgets, and the one for a scope (all providers, or one).</summary>
    public interface ICloudBudgetRepository : IRepository<CloudBudgetAggregate, Guid>
    {
        Task<IReadOnlyList<CloudBudgetAggregate>> ListForSiteAsync(int siteId, CancellationToken ct = default);

        Task<CloudBudgetAggregate?> FindForScopeAsync(int siteId, CloudProvider? provider, CancellationToken ct = default);
    }

    /// <summary>The alert markers' closed repository: whether an alert key was already raised for a site.</summary>
    public interface ICloudCostAlertMarkerRepository : IRepository<CloudCostAlertMarkerAggregate, Guid>
    {
        Task<bool> WasRaisedAsync(Tenancy.Domain.TenantId tenantId, int siteId, string key, CancellationToken ct = default);
    }

    public sealed class CloudCostRecordRepository(IRepository<CloudCostRecordAggregate, Guid> inner) : ICloudCostRecordRepository
    {
        public Task<CloudCostRecordAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<CloudCostRecordAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync(CloudCostRecordAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(CloudCostRecordAggregate aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(CloudCostRecordAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<CloudCostRecordAggregate>> FindAsync(Expression<Func<CloudCostRecordAggregate, bool>> predicate, CancellationToken ct = default) => inner.FindAsync(predicate, ct);

        public async Task<IReadOnlyList<CloudCostRecordAggregate>> ListForSiteAsync(int siteId, DateTime? fromUtc = null, DateTime? toUtcExclusive = null, CancellationToken ct = default)
        {
            IReadOnlyList<CloudCostRecordAggregate> rows;
            if (fromUtc is { } from && toUtcExclusive is { } to)
            {
                rows = await inner.FindAsync(record => record.SiteId == siteId && record.DateUtc >= from && record.DateUtc < to, ct);
            }
            else if (fromUtc is { } lower)
            {
                rows = await inner.FindAsync(record => record.SiteId == siteId && record.DateUtc >= lower, ct);
            }
            else if (toUtcExclusive is { } upper)
            {
                rows = await inner.FindAsync(record => record.SiteId == siteId && record.DateUtc < upper, ct);
            }
            else
            {
                rows = await inner.FindAsync(record => record.SiteId == siteId, ct);
            }

            return rows.OrderBy(record => record.DateUtc).ThenBy(record => record.Provider).ThenBy(record => record.Service, StringComparer.Ordinal).ToList();
        }
    }

    public sealed class CloudBudgetRepository(IRepository<CloudBudgetAggregate, Guid> inner) : ICloudBudgetRepository
    {
        public Task<CloudBudgetAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<CloudBudgetAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync(CloudBudgetAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(CloudBudgetAggregate aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(CloudBudgetAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<CloudBudgetAggregate>> FindAsync(Expression<Func<CloudBudgetAggregate, bool>> predicate, CancellationToken ct = default) => inner.FindAsync(predicate, ct);

        public async Task<IReadOnlyList<CloudBudgetAggregate>> ListForSiteAsync(int siteId, CancellationToken ct = default) =>
            (await inner.FindAsync(budget => budget.SiteId == siteId, ct)).OrderBy(budget => budget.Provider.HasValue).ThenBy(budget => budget.Provider).ToList();

        // The provider scope is compared in memory: a nullable enum in a predicate is outside the FindAsync safe set.
        public async Task<CloudBudgetAggregate?> FindForScopeAsync(int siteId, CloudProvider? provider, CancellationToken ct = default) =>
            (await ListForSiteAsync(siteId, ct)).FirstOrDefault(budget => budget.Provider == provider);
    }

    public sealed class CloudCostAlertMarkerRepository(IRepository<CloudCostAlertMarkerAggregate, Guid> inner) : ICloudCostAlertMarkerRepository
    {
        public Task<CloudCostAlertMarkerAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<CloudCostAlertMarkerAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync(CloudCostAlertMarkerAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(CloudCostAlertMarkerAggregate aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(CloudCostAlertMarkerAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<CloudCostAlertMarkerAggregate>> FindAsync(Expression<Func<CloudCostAlertMarkerAggregate, bool>> predicate, CancellationToken ct = default) => inner.FindAsync(predicate, ct);

        public async Task<bool> WasRaisedAsync(Tenancy.Domain.TenantId tenantId, int siteId, string key, CancellationToken ct = default) =>
            await inner.GetByIdAsync(CloudCostAlertMarkerAggregate.BuildId(tenantId, siteId, key), ct) is not null;
    }
}
