using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Documentation;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.EfCore;
using Stomme.Shared;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Stomme.Modules.HtmlText.Repository
{
    /// <summary>
    /// The relational (EF Core) <see cref="IHtmlTextStore"/> — the seam's own
    /// <see cref="EfRepository{TAggregate, TId}"/> over the module's <see cref="HtmlTextContext"/>. Mirrors
    /// the framework's <c>EfSiteStore</c>: a context is created per store instance from
    /// <see cref="IDbContextFactory{TContext}"/> rather than resolved from the container, because
    /// <see cref="HtmlTextContext"/> resolves its tenant connection lazily in <c>OnConfiguring</c> and a cached
    /// context would bind to the first tenant a scheduled job touched.
    /// </summary>
    [PrivateApi("Mark HtmlText classes as private, since it's not very useful in the public docs")]
    internal sealed class EfHtmlTextStore : IHtmlTextStore, IDisposable
    {
        private readonly HtmlTextContext _context;
        private readonly IRepository<HtmlTextAggregate, int> _repository;

        public EfHtmlTextStore(
            IDbContextFactory<HtmlTextContext> contextFactory,
            IAggregateTracker aggregateTracker,
            ITenantContext tenantContext,
            IEnumerable<IDomainEventSerializer> serializers)
        {
            _context = contextFactory.CreateDbContext();
            _repository = new EfRepository<HtmlTextAggregate, int>(_context, aggregateTracker, tenantContext, serializers);
        }

        public Task<HtmlTextAggregate> GetByIdAsync(int id, CancellationToken ct = default)
            => _repository.GetByIdAsync(id, ct);

        public Task<IReadOnlyList<HtmlTextAggregate>> GetAllAsync(CancellationToken ct = default)
            => _repository.GetAllAsync(ct);

        public Task AddAsync(HtmlTextAggregate aggregate, CancellationToken ct = default)
            => _repository.AddAsync(aggregate, ct);

        public Task UpdateAsync(HtmlTextAggregate aggregate, CancellationToken ct = default)
            => _repository.UpdateAsync(aggregate, ct);

        public Task DeleteAsync(HtmlTextAggregate aggregate, CancellationToken ct = default)
            => _repository.DeleteAsync(aggregate, ct);

        public void Dispose() => _context.Dispose();
    }

    /// <summary>
    /// The relational <see cref="IHtmlTextStoreSessionFactory"/>: EF Core document/query sessions over a fresh
    /// per-session <see cref="HtmlTextContext"/> (the session owns and disposes it), for the same tenant-correctness
    /// reason as <see cref="EfHtmlTextStore"/>. The module's twin of the framework's
    /// <c>FrameworkStoreSessionFactory</c>.
    /// </summary>
    [PrivateApi("Mark HtmlText classes as private, since it's not very useful in the public docs")]
    internal sealed class EfHtmlTextStoreSessionFactory : IHtmlTextStoreSessionFactory
    {
        private readonly EfDocumentSessionFactory _factory;

        public EfHtmlTextStoreSessionFactory(IDbContextFactory<HtmlTextContext> contextFactory)
        {
            _factory = new EfDocumentSessionFactory(new Dictionary<string, Func<DbContext>>(StringComparer.OrdinalIgnoreCase)
            {
                [StoreNames.Main] = () => contextFactory.CreateDbContext()
            });
        }

        public IQuerySession OpenQuerySession(string storeName = StoreNames.Main) => _factory.OpenQuerySession(storeName);

        public IDocumentSession OpenSession(string storeName = StoreNames.Main) => _factory.OpenSession(storeName);
    }
}
