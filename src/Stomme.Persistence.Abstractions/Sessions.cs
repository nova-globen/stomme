namespace Stomme.Persistence.Abstractions;

/// <summary>
/// A read session with identity-map semantics: loading the same entity twice within one session returns
/// the same instance. Store plugins back this with their native session (EF Core's change tracker,
/// RavenDB's document session). Use for read models and queries that should not flow through an aggregate
/// repository. Dispose releases the underlying session.
/// </summary>
public interface IQuerySession : IAsyncDisposable
{
    /// <summary>Loads an entity by id, or null if absent. Repeated loads return the identity-mapped instance.</summary>
    Task<TEntity?> LoadAsync<TEntity>(object id, CancellationToken cancellationToken = default)
        where TEntity : class;

    /// <summary>A queryable over the entity set for composing read queries.</summary>
    IQueryable<TEntity> Query<TEntity>()
        where TEntity : class;
}

/// <summary>
/// A read-write session: stages writes against the identity map and flushes them atomically on
/// <see cref="SaveChangesAsync"/>. The read-side contract is inherited from <see cref="IQuerySession"/>.
/// </summary>
public interface IDocumentSession : IQuerySession
{
    /// <summary>Stages an insert or update of <paramref name="entity"/>.</summary>
    void Store<TEntity>(TEntity entity)
        where TEntity : class;

    /// <summary>Stages a delete of <paramref name="entity"/>.</summary>
    void Delete<TEntity>(TEntity entity)
        where TEntity : class;

    /// <summary>Flushes all staged changes in a single transaction.</summary>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Opens sessions bound to a named store (see <see cref="StoreNames"/>). The default store is
/// <see cref="StoreNames.Main"/>; high-volume execution logs may live in a separate
/// <see cref="StoreNames.Logs"/> store (bound by the Pipelines feature). Resolve the store name for an
/// entity type with <see cref="IStoreBindingResolver"/>.
/// </summary>
public interface IDocumentSessionFactory
{
    /// <summary>Opens a read-only session against the named store.</summary>
    IQuerySession OpenQuerySession(string storeName = StoreNames.Main);

    /// <summary>Opens a read-write session against the named store.</summary>
    IDocumentSession OpenSession(string storeName = StoreNames.Main);
}
