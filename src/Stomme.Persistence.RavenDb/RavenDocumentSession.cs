using Stomme.Persistence.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Exceptions;
using RavenSession = Raven.Client.Documents.Session.IDocumentSession;

namespace Stomme.Persistence.RavenDb;

/// <summary>
/// RavenDB <see cref="IDocumentSession"/> — the seam's read path (D19: every framework-store read goes
/// through <see cref="IQuerySession"/>, never through the aggregate repository).
/// <para>
/// <b>Why RavenDB's synchronous session backs this.</b> <see cref="IQuerySession.Query{TEntity}"/> hands
/// back a bare <see cref="IQueryable{T}"/> and every caller on the seam materializes it with plain LINQ
/// <c>.ToList()</c> (deliberately — EF Core's <c>ToListAsync</c> would bind the read to the relational
/// provider, which D7 forbids). RavenDB's <i>async</i> session exposes a LINQ provider that only executes
/// asynchronously, so a synchronous <c>.ToList()</c> over it throws. The synchronous session's provider
/// executes in-place and satisfies the contract as written.
/// </para>
/// <para>
/// <b>Staleness.</b> RavenDB indexes are updated asynchronously, so a query issued right after a write may
/// not observe it — silently, as an empty result rather than an error. Sessions opened with
/// <paramref name="waitForNonStaleResults"/> block until the index catches up. See
/// src/Stomme.Persistence.RavenDb/README.md for where real code needs this.
/// </para>
/// </summary>
public sealed class RavenDocumentSession(
    RavenSession session,
    IRavenIdGenerator? idGenerator = null,
    string? database = null,
    bool waitForNonStaleResults = false) : IDocumentSession
{
    // Entities staged by Store() whose id the database must assign. Their document id cannot be derived
    // until the id exists, so they are held here and resolved in SaveChangesAsync (Store is synchronous;
    // id generation is not).
    private readonly List<object> _awaitingId = [];

    public Task<TEntity?> LoadAsync<TEntity>(object id, CancellationToken cancellationToken = default)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(id);
        cancellationToken.ThrowIfCancellationRequested();
        // Takes the entity's key, mirroring the relational provider's Set<T>().FindAsync(key); the document
        // id is derived by the shared convention. Repeated loads return the identity-mapped instance.
        return Task.FromResult<TEntity?>(session.Load<TEntity>(RavenDocumentIds.For(typeof(TEntity), id)));
    }

    public IQueryable<TEntity> Query<TEntity>()
        where TEntity : class
    {
        var query = session.Query<TEntity>();
        var customized = waitForNonStaleResults
            ? query.Customize(customization => customization.WaitForNonStaleResults())
            : query;

        // Adapt standard LINQ to RavenDB's dialect before RavenDB's provider sees it. The seam hands callers a
        // bare IQueryable and D7 forbids them knowing the provider, so `values.Contains(doc.Field)` — which
        // RavenDB's provider rejects outright — is translated here rather than spelled RavenDB's way upstream.
        // See RavenLinqRewriting.
        return customized.Rewriting();
    }

    public void Store<TEntity>(TEntity entity)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (NeedsGeneratedId(entity))
        {
            _awaitingId.Add(entity);
            return;
        }

        session.Store(entity, RavenDocumentIds.For(entity));
    }

    public void Delete<TEntity>(TEntity entity)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entity);
        // By document id rather than by instance: RavenDB's Delete(entity) requires the instance to be in
        // this session's identity map and throws otherwise, whereas the relational provider's Remove()
        // accepts a detached entity and deletes by key. Deleting by id matches that contract for both.
        session.Delete(RavenDocumentIds.For(entity));
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entity in _awaitingId)
        {
            var idType = RavenDocumentIds.IdValue(entity)!.GetType();
            var id = await idGenerator!
                .NextIdAsync(entity.GetType().Name, idType, database, cancellationToken)
                .ConfigureAwait(false);
            RavenDocumentIds.SetId(entity, id);
            session.Store(entity, RavenDocumentIds.For(entity));
        }

        _awaitingId.Clear();

        if (waitForNonStaleResults)
        {
            // Block the save until the indexes covering these documents have caught up, so a read issued
            // after this call observes the write. Install/seed paths are read-after-write throughout.
            session.Advanced.WaitForIndexesAfterSaveChanges();
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            session.SaveChanges();
        }
        catch (ConcurrencyException)
        {
            // Surface the seam's conflict, matching the relational provider's DbUpdateConcurrencyException
            // translation, so callers catch one exception type across providers.
            throw new ConcurrencyConflictException(nameof(IDocumentSession), string.Empty);
        }
    }

    public ValueTask DisposeAsync()
    {
        session.Dispose();
        return ValueTask.CompletedTask;
    }

    private bool NeedsGeneratedId(object entity)
    {
        if (idGenerator is null || RavenDocumentIds.IdValue(entity) is not { } id)
        {
            return false;
        }

        var idType = id.GetType();
        return idGenerator.CanGenerate(idType)
            && Equals(id, Activator.CreateInstance(idType));
    }
}
