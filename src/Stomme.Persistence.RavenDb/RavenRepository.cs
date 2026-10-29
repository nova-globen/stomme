using System.Linq.Expressions;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

namespace Stomme.Persistence.RavenDb;

/// <summary>
/// RavenDB <see cref="IRepository{TAggregate, TId}"/>. Documents are addressed as
/// <c>{Collection}/{id}</c>; the aggregate's typed/VO id is stored as a normal field (RavenDB's
/// identity-property convention is disabled in <see cref="RavenPersistenceExtensions"/>). Tenant
/// isolation filters by the ambient <see cref="ITenantContext"/>.
/// </summary>
public sealed class RavenRepository<TAggregate, TId>(
    IDocumentStore store,
    ITenantContext tenantContext,
    IAggregateTracker aggregateTracker,
    IEnumerable<IDomainEventSerializer> serializers,
    RavenChangeVectorTracker? changeVectors = null,
    IRavenTenantDatabaseResolver? databaseResolver = null,
    IFieldProtector? fieldProtector = null,
    IRavenIdGenerator? idGenerator = null)
    : IRepository<TAggregate, TId>
    where TAggregate : AggregateRoot<TId>
    where TId : notnull
{
    // When an outbox serializer is registered, capture domain events transactionally; otherwise the
    // unit of work dispatches them in-process after the save.
    private readonly IDomainEventSerializer? _serializer = serializers.FirstOrDefault();

    private static string DocId(TId id) => RavenDocumentIds.For(typeof(TAggregate), id);

    // Opens a session against the current tenant's database (database-per-tenant isolation); a null
    // resolution uses the store's default database (shared-database isolation + the tenant query filter).
    private IAsyncDocumentSession OpenSession()
    {
        var database = databaseResolver?.Resolve(tenantContext.CurrentTenantId);
        return string.IsNullOrEmpty(database)
            ? store.OpenAsyncSession()
            : store.OpenAsyncSession(new SessionOptions { Database = database });
    }

    public async Task<TAggregate?> GetByIdAsync(TId id, CancellationToken ct = default)
    {
        using var session = OpenSession();
        var document = await session.LoadAsync<TAggregate>(DocId(id), ct);
        if (document is null || !IsVisible(document))
        {
            return null;
        }

        // Remember the change vector so a later save in this scope can enforce optimistic concurrency.
        changeVectors?.Remember(DocId(id), session.Advanced.GetChangeVectorFor(document));
        fieldProtector?.UnprotectFields(document); // decrypt [Encrypt] fields for the caller
        return document;
    }

    public async Task<IReadOnlyList<TAggregate>> GetAllAsync(CancellationToken ct = default)
    {
        using var session = OpenSession();
        var all = await session.Query<TAggregate>()
            .Customize(c => c.WaitForNonStaleResults())
            .ToListAsync(ct);

        var visible = all.Where(IsVisible).ToList();
        foreach (var document in visible)
        {
            changeVectors?.Remember(DocId(document.Id), session.Advanced.GetChangeVectorFor(document));
            fieldProtector?.UnprotectFields(document); // decrypt [Encrypt] fields for the caller
        }

        return visible;
    }

    // Translates the predicate to a RavenDB indexed session query (auto-index on the queried fields) so the
    // filter runs in the store, then applies the same tenant visibility/change-vector/decrypt post-processing
    // GetAllAsync does. WaitForNonStaleResults keeps a read after a write in the same flow deterministic. Safe
    // only for the non-string predicates the IRepository.FindAsync contract documents (a bare string == is
    // case-INSENSITIVE here but case-SENSITIVE on EF/PostgreSQL — normalize both sides for text).
    public async Task<IReadOnlyList<TAggregate>> FindAsync(
        Expression<Func<TAggregate, bool>> predicate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        using var session = OpenSession();
        // The predicate is rewritten into RavenDB's dialect first - the framework seam gets this through
        // IQuerySession's wrapping provider, and without it the module seam threw on ids.Contains(...).
        var matches = await session.Query<TAggregate>()
            .Customize(c => c.WaitForNonStaleResults())
            .Where(RavenLinqRewriting.RewritePredicate(predicate))
            .ToListAsync(ct);

        var visible = matches.Where(IsVisible).ToList();
        foreach (var document in visible)
        {
            changeVectors?.Remember(DocId(document.Id), session.Advanced.GetChangeVectorFor(document));
            fieldProtector?.UnprotectFields(document); // decrypt [Encrypt] fields for the caller
        }

        return visible;
    }

    public async Task AddAsync(TAggregate aggregate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        await AssignIdAsync(aggregate, ct);
        await PersistAsync(aggregate, ct);
    }

    public Task UpdateAsync(TAggregate aggregate, CancellationToken ct = default) => PersistAsync(aggregate, ct);

    // The framework store's inserts leave Id unset for the database to assign (the relational model maps it
    // ValueGeneratedOnAdd). Without this, every such insert addressed {TypeName}/0 and blind-upserted onto
    // the previous one — no error, no conflict, just a lost document and an Id of 0 handed back to the
    // caller, which then propagated into alias.SiteId, page.ParentId, userRole.RoleId...
    // Only on Add: an update carrying a default id is a caller bug, and minting an id would hide it.
    private async Task AssignIdAsync(TAggregate aggregate, CancellationToken ct)
    {
        if (idGenerator is null
            || !idGenerator.CanGenerate(typeof(TId))
            || !EqualityComparer<TId>.Default.Equals(aggregate.Id, default!))
        {
            return;
        }

        var database = databaseResolver?.Resolve(tenantContext.CurrentTenantId);
        var id = await idGenerator.NextIdAsync(typeof(TAggregate).Name, typeof(TId), database, ct);
        RavenDocumentIds.SetId(aggregate, id);
    }

    public async Task DeleteAsync(TAggregate aggregate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        using var session = OpenSession();
        session.Delete(DocId(aggregate.Id));

        // Dispatch the aggregate's domain events for the deletion the same way a save does: transactionally
        // via the outbox when enabled, otherwise in-process through the unit of work after the delete commits.
        if (_serializer is not null)
        {
            foreach (var message in CaptureOutbox(aggregate))
            {
                await session.StoreAsync(message, $"OutboxMessages/{message.Id}", ct);
            }
        }

        await session.SaveChangesAsync(ct);

        if (_serializer is not null)
        {
            aggregate.ClearDomainEvents();
        }
        else
        {
            aggregateTracker.Track(aggregate);
        }
    }

    // Store the aggregate and — when the outbox is enabled — its domain events as outbox documents in
    // the same session, so they commit atomically with the aggregate.
    private async Task PersistAsync(TAggregate aggregate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        var documentId = DocId(aggregate.Id);
        using var session = OpenSession();

        // When the aggregate was loaded in this scope, save conditionally on its change vector so a
        // concurrent write since the load is rejected. Otherwise a blind upsert (a null change vector
        // means no concurrency check) preserves the prior behavior for save-without-load flows.
        var expectedChangeVector = changeVectors?.Get(documentId);
        fieldProtector?.ProtectFields(aggregate); // encrypt [Encrypt] fields so the stored document is ciphertext
        await session.StoreAsync(aggregate, expectedChangeVector, documentId, ct);

        if (_serializer is not null)
        {
            foreach (var message in CaptureOutbox(aggregate))
            {
                await session.StoreAsync(message, $"OutboxMessages/{message.Id}", ct);
            }
        }

        try
        {
            await session.SaveChangesAsync(ct);
        }
        catch (ConcurrencyException)
        {
            throw new ConcurrencyConflictException(typeof(TAggregate).Name, aggregate.Id.ToString() ?? string.Empty);
        }

        // Refresh the remembered change vector so a subsequent save in this scope stays consistent.
        changeVectors?.Remember(documentId, session.Advanced.GetChangeVectorFor(aggregate));
        fieldProtector?.UnprotectFields(aggregate); // restore the caller's instance to plaintext

        if (_serializer is not null)
        {
            aggregate.ClearDomainEvents();
        }
        else
        {
            aggregateTracker.Track(aggregate);
        }
    }

    private IEnumerable<OutboxMessage> CaptureOutbox(TAggregate aggregate)
    {
        var tenantId = (aggregate as ITenantOwned)?.TenantId.Value ?? tenantContext.CurrentTenantId?.Value;
        foreach (var domainEvent in aggregate.DomainEvents)
        {
            var (eventType, payload) = _serializer!.Serialize(domainEvent);
            yield return new OutboxMessage
            {
                Id = Guid.NewGuid(),
                EventType = eventType,
                Payload = payload,
                TenantId = tenantId,
                OccurredOnUtc = domainEvent.OccurredOn,
            };
        }
    }

    private bool IsVisible(TAggregate aggregate) =>
        aggregate is not ITenantOwned owned
        || tenantContext.IsSystemScope
        || Equals(tenantContext.CurrentTenantId, owned.TenantId);
}
