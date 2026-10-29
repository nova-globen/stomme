#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Erasure.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Erasure.Persistence
{
    /// <summary>The erasure requests and receipts of the installation, storage-blind: the closed repository plus the reads the job and the Host need.</summary>
    public interface IErasureRequestRepository : IRepository<ErasureRequestAggregate, Guid>
    {
        /// <summary>The runs a site still owes: pending, and retaining ones whose earliest retention fell due.</summary>
        Task<IReadOnlyList<ErasureRequestAggregate>> ListOpenAsync(int siteId, DateTime nowUtc, CancellationToken ct = default);

        Task<IReadOnlyList<ErasureRequestAggregate>> ListForSiteAsync(int siteId, int take, CancellationToken ct = default);
    }

    public sealed class ErasureRequestRepository(IRepository<ErasureRequestAggregate, Guid> inner) : IErasureRequestRepository
    {
        public Task<ErasureRequestAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<ErasureRequestAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task<IReadOnlyList<ErasureRequestAggregate>> FindAsync(System.Linq.Expressions.Expression<Func<ErasureRequestAggregate, bool>> predicate, CancellationToken ct = default) => inner.FindAsync(predicate, ct);

        public Task AddAsync(ErasureRequestAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(ErasureRequestAggregate aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(ErasureRequestAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public async Task<IReadOnlyList<ErasureRequestAggregate>> ListOpenAsync(int siteId, DateTime nowUtc, CancellationToken ct = default)
        {
            // Equality on ints and enums only (the safe subset of the FindAsync contract); the date bound is applied in memory.
            var pending = await inner.FindAsync(r => r.SiteId == siteId && r.Status == ErasureRequestStatus.Pending, ct);
            var retaining = await inner.FindAsync(r => r.SiteId == siteId && r.Status == ErasureRequestStatus.Retaining, ct);
            return pending.Concat(retaining.Where(r => r.RetainedUntilUtc is null || r.ReadOutcomes().Any(o => o.RetainedUntilUtc is { } due && o.PurgedOnUtc is null && due <= nowUtc)))
                .OrderBy(r => r.RequestedOnUtc)
                .ToList();
        }

        public async Task<IReadOnlyList<ErasureRequestAggregate>> ListForSiteAsync(int siteId, int take, CancellationToken ct = default) =>
            (await inner.FindAsync(r => r.SiteId == siteId, ct)).OrderByDescending(r => r.RequestedOnUtc).Take(Math.Max(1, take)).ToList();
    }
}
