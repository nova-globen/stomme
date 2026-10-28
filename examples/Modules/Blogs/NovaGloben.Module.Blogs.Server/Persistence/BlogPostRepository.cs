using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.SharedKernel.Domain;
using NovaGloben.Module.Blogs.Domain;

namespace NovaGloben.Module.Blogs.Persistence
{
    /// <summary>
    /// The Blogs module's closed repository — PROVIDER-NEUTRAL by construction. Every member, including the
    /// by-module read, DELEGATES to the CLOSED <see cref="IRepository{TAggregate, TId}"/> the module registered
    /// through <c>AddModuleAggregatePersistence&lt;BlogPost, Guid&gt;()</c> — an <c>EfRepository</c> over the
    /// host's shared <c>ModuleAggregatesDbContext</c> on a relational install, a <c>RavenRepository</c> on a
    /// RavenDB install, or a <c>FileRepository</c> on a file install. It holds NO provider-specific code and NO
    /// <c>DbContext</c>, so a single implementation runs on EVERY provider — the storage-blind guarantee.
    /// <para>
    /// <see cref="GetByModuleAsync"/> filters through <see cref="IRepository{TAggregate, TId}.FindAsync"/> on an
    /// <c>int</c> equality (<c>ModuleId</c>). The filter runs in the store; the newest-first ORDER is applied in
    /// memory over the already-filtered, single-blog result set, which keeps the neutral query to its one safe
    /// job (int equality) and off the string predicates that diverge across providers.
    /// </para>
    /// </summary>
    public sealed class BlogPostRepository : IBlogPostRepository
    {
        private readonly IRepository<BlogPost, Guid> _inner;

        public BlogPostRepository(IRepository<BlogPost, Guid> inner) => _inner = inner;

        public Task<BlogPost> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            _inner.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<BlogPost>> GetAllAsync(CancellationToken ct = default) =>
            _inner.GetAllAsync(ct);

        public Task AddAsync(BlogPost aggregate, CancellationToken ct = default) =>
            _inner.AddAsync(aggregate, ct);

        public Task UpdateAsync(BlogPost aggregate, CancellationToken ct = default) =>
            _inner.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(BlogPost aggregate, CancellationToken ct = default) =>
            _inner.DeleteAsync(aggregate, ct);

        public Task<IReadOnlyList<BlogPost>> FindAsync(
            Expression<Func<BlogPost, bool>> predicate, CancellationToken ct = default) =>
            _inner.FindAsync(predicate, ct);

        public async Task<IReadOnlyList<BlogPost>> GetByModuleAsync(int moduleId, CancellationToken ct = default)
        {
            var posts = await _inner.FindAsync(post => post.ModuleId == moduleId, ct);
            return posts
                .OrderByDescending(post => post.PublishedOnUtc)
                .ToList();
        }
    }
}
