#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Portability.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Portability.Persistence
{
    /// <summary>
    /// The package rows' reads (BL-82, D107 item 5). Filters are on <c>SiteId</c>, <c>Status</c>, <c>Kind</c>,
    /// <c>SubjectUserId</c> and the UTC instants only - ints, enums and ranges, the provider-safe set (D78).
    /// </summary>
    public interface IPortabilityPackageRepository : IRepository<PortabilityPackageAggregate, Guid>
    {
        /// <summary>One package of THIS site, or null - the same answer for "no such package" and "another site's".</summary>
        Task<PortabilityPackageAggregate?> FindForSiteAsync(int siteId, Guid id, CancellationToken ct = default);

        /// <summary>A site's packages, newest first, at most <paramref name="take"/>.</summary>
        Task<IReadOnlyList<PortabilityPackageAggregate>> ListForSiteAsync(int siteId, int take, CancellationToken ct = default);

        /// <summary>A site's packages about one person, newest first.</summary>
        Task<IReadOnlyList<PortabilityPackageAggregate>> ListForSubjectAsync(int siteId, int subjectUserId, CancellationToken ct = default);

        /// <summary>A site's packages in one status, oldest first - the build queue when the status is Building.</summary>
        Task<IReadOnlyList<PortabilityPackageAggregate>> ListByStatusAsync(int siteId, PortabilityPackageStatus status, CancellationToken ct = default);

        /// <summary>A site's packages whose expiry has passed and that are not already Expired - what retention retires.</summary>
        Task<IReadOnlyList<PortabilityPackageAggregate>> ListDueForExpiryAsync(int siteId, DateTime nowUtc, CancellationToken ct = default);
    }

    public sealed class PortabilityPackageRepository(IRepository<PortabilityPackageAggregate, Guid> inner) : IPortabilityPackageRepository
    {
        public Task<PortabilityPackageAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<PortabilityPackageAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync(PortabilityPackageAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(PortabilityPackageAggregate aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(PortabilityPackageAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<PortabilityPackageAggregate>> FindAsync(Expression<Func<PortabilityPackageAggregate, bool>> predicate, CancellationToken ct = default) =>
            inner.FindAsync(predicate, ct);

        public async Task<PortabilityPackageAggregate?> FindForSiteAsync(int siteId, Guid id, CancellationToken ct = default)
        {
            var package = await inner.GetByIdAsync(id, ct);
            return package is not null && package.SiteId == siteId ? package : null;
        }

        public async Task<IReadOnlyList<PortabilityPackageAggregate>> ListForSiteAsync(int siteId, int take, CancellationToken ct = default) =>
            (await inner.FindAsync(package => package.SiteId == siteId, ct))
                .OrderByDescending(package => package.CreatedOnUtc).Take(Math.Max(1, take)).ToList();

        public async Task<IReadOnlyList<PortabilityPackageAggregate>> ListForSubjectAsync(int siteId, int subjectUserId, CancellationToken ct = default) =>
            (await inner.FindAsync(package => package.SiteId == siteId && package.SubjectUserId == subjectUserId, ct))
                .OrderByDescending(package => package.CreatedOnUtc).ToList();

        public async Task<IReadOnlyList<PortabilityPackageAggregate>> ListByStatusAsync(int siteId, PortabilityPackageStatus status, CancellationToken ct = default) =>
            (await inner.FindAsync(package => package.SiteId == siteId && package.Status == status, ct))
                .OrderBy(package => package.CreatedOnUtc).ToList();

        public async Task<IReadOnlyList<PortabilityPackageAggregate>> ListDueForExpiryAsync(int siteId, DateTime nowUtc, CancellationToken ct = default)
        {
            var now = UtcOnly(nowUtc);
            return (await inner.FindAsync(package => package.SiteId == siteId && package.ExpiresOnUtc < now, ct))
                .Where(package => package.Status != PortabilityPackageStatus.Expired)
                .ToList();
        }

        private static DateTime UtcOnly(DateTime value) =>
            value.Kind == DateTimeKind.Utc ? value : throw new ArgumentException($"The instant must be UTC (Kind was {value.Kind}).", nameof(value));
    }

    /// <summary>The portability audit's reads (D107 item 5). Append-only.</summary>
    public interface IPortabilityAuditRepository : IRepository<PortabilityAuditEntryAggregate, Guid>
    {
        Task<IReadOnlyList<PortabilityAuditEntryAggregate>> ListRecentAsync(int siteId, DateTime sinceUtc, int take, CancellationToken ct = default);

        Task<IReadOnlyList<PortabilityAuditEntryAggregate>> ListOlderThanAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default);
    }

    public sealed class PortabilityAuditRepository(IRepository<PortabilityAuditEntryAggregate, Guid> inner) : IPortabilityAuditRepository
    {
        public Task<PortabilityAuditEntryAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<PortabilityAuditEntryAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task AddAsync(PortabilityAuditEntryAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(PortabilityAuditEntryAggregate aggregate, CancellationToken ct = default) =>
            throw new InvalidOperationException("The portability audit is append-only.");

        public Task DeleteAsync(PortabilityAuditEntryAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<PortabilityAuditEntryAggregate>> FindAsync(Expression<Func<PortabilityAuditEntryAggregate, bool>> predicate, CancellationToken ct = default) =>
            inner.FindAsync(predicate, ct);

        public async Task<IReadOnlyList<PortabilityAuditEntryAggregate>> ListRecentAsync(int siteId, DateTime sinceUtc, int take, CancellationToken ct = default)
        {
            var since = UtcOnly(sinceUtc);
            return (await inner.FindAsync(entry => entry.SiteId == siteId && entry.OccurredOnUtc >= since, ct))
                .OrderByDescending(entry => entry.OccurredOnUtc).Take(Math.Max(1, take)).ToList();
        }

        public Task<IReadOnlyList<PortabilityAuditEntryAggregate>> ListOlderThanAsync(int siteId, DateTime cutoffUtc, CancellationToken ct = default)
        {
            var cutoff = UtcOnly(cutoffUtc);
            return inner.FindAsync(entry => entry.SiteId == siteId && entry.OccurredOnUtc < cutoff, ct);
        }

        private static DateTime UtcOnly(DateTime value) =>
            value.Kind == DateTimeKind.Utc ? value : throw new ArgumentException($"The instant must be UTC (Kind was {value.Kind}).", nameof(value));
    }
}
