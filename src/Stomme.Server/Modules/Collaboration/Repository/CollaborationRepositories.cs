#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Collaboration;
using Stomme.Modules.Collaboration.Domain;
using Stomme.SharedKernel.Domain;

namespace Stomme.Collaboration.Persistence
{
    /// <summary>
    /// The locks, read by site. Every query filters on the SITE (an int - provider-neutral, D78) and matches the resource in
    /// memory with an ordinal comparison: a string equality in a query splits the providers (case-sensitive on EF and the
    /// File store, insensitive on RavenDB), and a site holds a handful of locks.
    /// </summary>
    public interface IResourceLockRepository : IRepository<ResourceLockAggregate, Guid>
    {
        /// <summary>The site's locks on the resource at the scope that are not released, the latest lease first.</summary>
        Task<IReadOnlyList<ResourceLockAggregate>> ListUnreleasedAsync(int siteId, ResourceRef resource, LockScope scope, CancellationToken ct = default);

        /// <summary>The site's locks on the resource at any scope that are not released.</summary>
        Task<IReadOnlyList<ResourceLockAggregate>> ListUnreleasedAsync(int siteId, ResourceRef resource, CancellationToken ct = default);

        /// <summary>Every lock of the site that is not released - what the sweep reads.</summary>
        Task<IReadOnlyList<ResourceLockAggregate>> ListUnreleasedAsync(int siteId, CancellationToken ct = default);

        /// <summary>Every lock the user held on the site, released or not - what an erasure removes.</summary>
        Task<IReadOnlyList<ResourceLockAggregate>> ListForUserAsync(int siteId, int userId, CancellationToken ct = default);
    }

    public sealed class ResourceLockRepository(IRepository<ResourceLockAggregate, Guid> inner) : IResourceLockRepository
    {
        public async Task<IReadOnlyList<ResourceLockAggregate>> ListUnreleasedAsync(int siteId, ResourceRef resource, LockScope scope, CancellationToken ct = default) =>
            (await ListUnreleasedAsync(siteId, resource, ct)).Where(candidate => candidate.Scope == scope).ToList();

        public async Task<IReadOnlyList<ResourceLockAggregate>> ListUnreleasedAsync(int siteId, ResourceRef resource, CancellationToken ct = default) =>
            (await ListUnreleasedAsync(siteId, ct))
                .Where(candidate => string.Equals(candidate.Resource.Value, resource.Value, StringComparison.Ordinal))
                .OrderByDescending(candidate => candidate.ExpiresUtc)
                .ToList();

        public async Task<IReadOnlyList<ResourceLockAggregate>> ListUnreleasedAsync(int siteId, CancellationToken ct = default) =>
            (await inner.FindAsync(candidate => candidate.SiteId == siteId, ct)).Where(candidate => !candidate.Released).ToList();

        public async Task<IReadOnlyList<ResourceLockAggregate>> ListForUserAsync(int siteId, int userId, CancellationToken ct = default) =>
            (await inner.FindAsync(candidate => candidate.SiteId == siteId && candidate.HolderUserId == userId, ct)).ToList();

        public Task<ResourceLockAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<ResourceLockAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task<IReadOnlyList<ResourceLockAggregate>> FindAsync(Expression<Func<ResourceLockAggregate, bool>> predicate, CancellationToken ct = default) => inner.FindAsync(predicate, ct);

        public Task AddAsync(ResourceLockAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(ResourceLockAggregate aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(ResourceLockAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);
    }

    /// <summary>The editing sessions, read by site; the resource matched in memory, ordinally (as the locks).</summary>
    public interface IEditSessionRepository : IRepository<EditSessionAggregate, Guid>
    {
        /// <summary>The site's open session on the resource - the earliest, should two ever be open.</summary>
        Task<EditSessionAggregate?> FindOpenAsync(int siteId, ResourceRef resource, CancellationToken ct = default);

        /// <summary>The session IF it belongs to the site; another site's answers as absent.</summary>
        Task<EditSessionAggregate?> GetForSiteAsync(Guid id, int siteId, CancellationToken ct = default);

        /// <summary>Every session of the site - what an erasure reads for the user's presence and changes.</summary>
        Task<IReadOnlyList<EditSessionAggregate>> ListForSiteAsync(int siteId, CancellationToken ct = default);
    }

    public sealed class EditSessionRepository(IRepository<EditSessionAggregate, Guid> inner) : IEditSessionRepository
    {
        public async Task<EditSessionAggregate?> FindOpenAsync(int siteId, ResourceRef resource, CancellationToken ct = default) =>
            (await ListForSiteAsync(siteId, ct))
                .Where(session => session.Status == EditSessionStatus.Open && string.Equals(session.Resource.Value, resource.Value, StringComparison.Ordinal))
                .OrderBy(session => session.StartedUtc)
                .FirstOrDefault();

        public async Task<EditSessionAggregate?> GetForSiteAsync(Guid id, int siteId, CancellationToken ct = default) =>
            await inner.GetByIdAsync(id, ct) is { } session && session.SiteId == siteId ? session : null;

        public Task<IReadOnlyList<EditSessionAggregate>> ListForSiteAsync(int siteId, CancellationToken ct = default) =>
            inner.FindAsync(session => session.SiteId == siteId, ct);

        public Task<EditSessionAggregate?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<EditSessionAggregate>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task<IReadOnlyList<EditSessionAggregate>> FindAsync(Expression<Func<EditSessionAggregate, bool>> predicate, CancellationToken ct = default) => inner.FindAsync(predicate, ct);

        public Task AddAsync(EditSessionAggregate aggregate, CancellationToken ct = default) => inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(EditSessionAggregate aggregate, CancellationToken ct = default) => inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(EditSessionAggregate aggregate, CancellationToken ct = default) => inner.DeleteAsync(aggregate, ct);
    }
}
