using System.Collections.Generic;
using Stomme.Documentation;
using Stomme.Persistence.Abstractions;
using Stomme.Persistence.RavenDb;
using Stomme.Repository.Stores;
using Stomme.Shared;
using Stomme.Tenancy.Abstractions;
using Raven.Client.Documents;

namespace Stomme.Modules.HtmlText.Repository
{
    /// <summary>
    /// The RavenDB <see cref="IHtmlTextStore"/> — the module's twin of the framework's <c>RavenSiteStore</c>,
    /// a thin binding of <see cref="HtmlTextAggregate"/> to <see cref="RavenFrameworkStore{TAggregate}"/> over the
    /// framework (tenant) RavenDB database.
    /// </summary>
    /// <remarks>
    /// Reuses the HOST singletons registered by the framework's <c>AddRavenFrameworkStore</c>
    /// (<see cref="IDocumentStore"/>, <see cref="IRavenTenantDatabaseResolver"/>, <see cref="IRavenIdGenerator"/>,
    /// <see cref="RavenChangeVectorTracker"/>, <see cref="IAggregateTracker"/>) — there is NO second RavenDB DI path
    /// (D17). The lazy <see cref="IDocumentStore"/> is never constructed on a relational install, because the
    /// provider selector never resolves this store there.
    /// </remarks>
    [PrivateApi("Mark HtmlText classes as private, since it's not very useful in the public docs")]
    internal sealed class RavenHtmlTextStore : RavenFrameworkStore<HtmlTextAggregate>, IHtmlTextStore
    {
        public RavenHtmlTextStore(
            IDocumentStore store,
            ITenantContext tenantContext,
            IAggregateTracker aggregateTracker,
            IEnumerable<IDomainEventSerializer> serializers,
            RavenChangeVectorTracker changeVectors,
            IRavenTenantDatabaseResolver databaseResolver,
            IRavenIdGenerator idGenerator)
            : base(store, tenantContext, aggregateTracker, serializers, changeVectors, databaseResolver, idGenerator)
        {
        }
    }

    /// <summary>
    /// The RavenDB <see cref="IHtmlTextStoreSessionFactory"/>: the module's read path over the framework
    /// database — the module's twin of the framework's <c>RavenFrameworkStoreSessionFactory</c>, bound to the same
    /// framework database (the tenant side D54 already ensured) via the shared <see cref="RavenFrameworkStoreOptions"/>
    /// host singleton.
    /// </summary>
    [PrivateApi("Mark HtmlText classes as private, since it's not very useful in the public docs")]
    internal sealed class RavenHtmlTextStoreSessionFactory : IHtmlTextStoreSessionFactory
    {
        private readonly RavenDocumentSessionFactory _factory;

        public RavenHtmlTextStoreSessionFactory(
            IDocumentStore store,
            RavenFrameworkStoreOptions options,
            IRavenIdGenerator idGenerator,
            ITenantContext tenantContext,
            IRavenTenantDatabaseResolver databaseResolver)
        {
            // The database of the tenant the session opens for - the one RavenHtmlTextStore writes to.
            _factory = new RavenDocumentSessionFactory(
                store,
                storeName => storeName.Equals(StoreNames.Main, System.StringComparison.OrdinalIgnoreCase)
                    ? RavenTenantRouting.DatabaseOf(databaseResolver, tenantContext, options)
                    : throw new System.InvalidOperationException($"No store is bound to the name '{storeName}'."),
                idGenerator,
                options.WaitForNonStaleResults);
        }

        public IQuerySession OpenQuerySession(string storeName = StoreNames.Main) => _factory.OpenQuerySession(storeName);

        public IDocumentSession OpenSession(string storeName = StoreNames.Main) => _factory.OpenSession(storeName);
    }
}
