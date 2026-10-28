#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.IdentityAudit.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.IdentityAudit.Persistence
{
    /// <summary>The identity audit ledger's reads (BL-120). Every filter is an int site id or a UTC range - the safe set.</summary>
    public interface IIdentityAuditRepository : IRepository<IdentityAuditEntryAggregate, Guid>
    {
        /// <summary>A site's entries at or after <paramref name="sinceUtc"/>, newest first, at most <paramref name="take"/>.</summary>
        Task<IReadOnlyList<IdentityAuditEntryAggregate>> ListRecentAsync(int siteId, DateTime sinceUtc, int take, CancellationToken ct = default);

        /// <summary>A site's entries older than <paramref name="cutoffUtc"/> - what retention deletes.</summary>
        Task<IReadOnlyList<IdentityAuditEntryAggregate>> ListOlderThanAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default);
    }

    /// <summary>The storage-blind implementation over the closed repository (D79/D80) - no provider code.</summary>
    public sealed class IdentityAuditRepository(IRepository<IdentityAuditEntryAggregate, Guid> inner) : IIdentityAuditRepository
    {
        public Task<IdentityAuditEntryAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<IdentityAuditEntryAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync(IdentityAuditEntryAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(IdentityAuditEntryAggregate aggregate, CancellationToken ct = default) =>
            throw new InvalidOperationException("The identity audit ledger is append-only.");

        public Task DeleteAsync(IdentityAuditEntryAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<IdentityAuditEntryAggregate>> FindAsync(Expression<Func<IdentityAuditEntryAggregate, bool>> predicate, CancellationToken ct = default) =>
            inner.FindAsync(predicate, ct);

        public async Task<IReadOnlyList<IdentityAuditEntryAggregate>> ListRecentAsync(int siteId, DateTime sinceUtc, int take, CancellationToken ct = default)
        {
            var since = UtcOnly(sinceUtc);
            var rows = await inner.FindAsync(entry => entry.SiteId == siteId && entry.OccurredOnUtc >= since, ct);
            return rows.OrderByDescending(entry => entry.OccurredOnUtc).Take(Math.Max(1, take)).ToList();
        }

        public Task<IReadOnlyList<IdentityAuditEntryAggregate>> ListOlderThanAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default)
        {
            var cutoff = UtcOnly(cutoffUtc);
            return inner.FindAsync(entry => entry.SiteId == siteId && entry.OccurredOnUtc < cutoff, ct);
        }

        private static DateTime UtcOnly(DateTime value) =>
            value.Kind == DateTimeKind.Utc ? value : throw new ArgumentException($"The instant must be UTC (Kind was {value.Kind}).", nameof(value));
    }
}
