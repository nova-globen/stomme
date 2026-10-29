#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Modules.Datasets.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Datasets.Persistence
{
    /// <summary>The Datasets module's repository - storage-blind over the closed <c>IRepository&lt;DatasetAggregate, Guid&gt;</c>; in the <c>Persistence</c> namespace the store-registration gate expects.</summary>
    public interface IDatasetRepository : IRepository<DatasetAggregate, Guid>
    {
        /// <summary>A site's dataset by normalised name: the same answer on every provider.</summary>
        Task<DatasetAggregate?> FindByNameAsync(int siteId, string name, CancellationToken ct = default);

        /// <summary>A site's dataset for an origin (the kind and the normalised reference - or the name, when the origin has no reference).</summary>
        Task<DatasetAggregate?> FindByOriginAsync(int siteId, OriginKind originKind, string reference, CancellationToken ct = default);

        /// <summary>A site's datasets, name order.</summary>
        Task<IReadOnlyList<DatasetAggregate>> ListForSiteAsync(int siteId, CancellationToken ct = default);
    }

    public sealed class DatasetRepository(IRepository<DatasetAggregate, Guid> inner) : IDatasetRepository
    {
        public Task<DatasetAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<DatasetAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync(DatasetAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(DatasetAggregate aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(DatasetAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<DatasetAggregate>> FindAsync(Expression<Func<DatasetAggregate, bool>> predicate, CancellationToken ct = default) => inner.FindAsync(predicate, ct);

        public async Task<DatasetAggregate?> FindByNameAsync(int siteId, string name, CancellationToken ct = default)
        {
            var normalized = DatasetAggregate.NormalizeName(name);
            return (await inner.FindAsync(dataset => dataset.SiteId == siteId && dataset.NameNormalized == normalized, ct)).FirstOrDefault();
        }

        public async Task<DatasetAggregate?> FindByOriginAsync(int siteId, OriginKind originKind, string reference, CancellationToken ct = default)
        {
            var normalized = DatasetAggregate.NormalizeReference(reference);
            return (await inner.FindAsync(dataset => dataset.SiteId == siteId && dataset.OriginKind == originKind && dataset.OriginReferenceNormalized == normalized, ct)).FirstOrDefault();
        }

        public async Task<IReadOnlyList<DatasetAggregate>> ListForSiteAsync(int siteId, CancellationToken ct = default) =>
            (await inner.FindAsync(dataset => dataset.SiteId == siteId, ct)).OrderBy(dataset => dataset.NameNormalized, StringComparer.Ordinal).ToList();
    }
}
