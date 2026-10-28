using Stomme.Persistence.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using IDocumentSession = Stomme.Persistence.Abstractions.IDocumentSession;

namespace Stomme.Persistence.RavenDb;

/// <summary>
/// Opens RavenDB sessions bound to a named store (<see cref="StoreNames"/>), mapping each store name to a
/// RavenDB database.
/// <para>
/// <b>Two databases, two factories (D23).</b> The Setting cluster reads across the framework store AND the
/// master store, so the host binds <c>IFrameworkStoreSessionFactory</c> and <c>IMasterStoreSessionFactory</c>
/// to two instances of this factory over two RavenDB databases. Nothing here assumes a single store.
/// </para>
/// <para>
/// <b>Staleness policy.</b> <paramref name="waitForNonStaleResults"/> makes every session opened by this
/// factory wait for indexes on save and query. It is intended for the install/seed path, which is
/// read-after-write throughout and fails silently when stale. Whether to enable it at runtime is a
/// performance decision the host owns, not a default this factory should impose.
/// </para>
/// </summary>
public sealed class RavenDocumentSessionFactory(
    IDocumentStore store,
    Func<string, string?> databaseOf,
    IRavenIdGenerator? idGenerator = null,
    bool waitForNonStaleResults = false) : IDocumentSessionFactory
{
    /// <summary>Binds each store name to a fixed database; a name not in the map is refused.</summary>
    public RavenDocumentSessionFactory(
        IDocumentStore store,
        IReadOnlyDictionary<string, string?> databases,
        IRavenIdGenerator? idGenerator = null,
        bool waitForNonStaleResults = false)
        : this(store, storeName => databases.TryGetValue(storeName, out var database) ? database : throw new InvalidOperationException($"No store is bound to the name '{storeName}'."), idGenerator, waitForNonStaleResults)
    {
    }

    /// <summary>Binds only <see cref="StoreNames.Main"/>, to the store's default database.</summary>
    public RavenDocumentSessionFactory(IDocumentStore store, IRavenIdGenerator? idGenerator = null, bool waitForNonStaleResults = false)
        : this(
            store,
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { [StoreNames.Main] = null },
            idGenerator,
            waitForNonStaleResults)
    {
    }

    public IQuerySession OpenQuerySession(string storeName = StoreNames.Main) => Open(storeName);

    public IDocumentSession OpenSession(string storeName = StoreNames.Main) => Open(storeName);

    private RavenDocumentSession Open(string storeName)
    {
        // Asked at every open (BL-176): the database of the tenant the session is opened for.
        var database = databaseOf(storeName);
        var session = string.IsNullOrEmpty(database)
            ? store.OpenSession()
            : store.OpenSession(new SessionOptions { Database = database });

        return new RavenDocumentSession(session, idGenerator, database, waitForNonStaleResults);
    }
}
