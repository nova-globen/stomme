using System.Linq.Expressions;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Stomme.Persistence.EfCore;

/// <summary>
/// EF Core <see cref="IRepository{TAggregate, TId}"/>. Reads honor the context's tenant query filter
/// (shared-schema isolation); a stale write surfaces as <see cref="ConcurrencyConflictException"/>.
/// Optimistic concurrency uses the provider concurrency token configured on the entity (e.g. xmin on
/// PostgreSQL). When an outbox serializer is registered, the aggregate's domain events are written as
/// outbox rows in the same SaveChanges (one transaction); otherwise the unit of work dispatches them.
/// </summary>
public sealed class EfRepository<TAggregate, TId>(
    DbContext context,
    IAggregateTracker aggregateTracker,
    ITenantContext tenantContext,
    IEnumerable<IDomainEventSerializer> serializers)
    : IRepository<TAggregate, TId>
    where TAggregate : AggregateRoot<TId>
    where TId : notnull
{
    private readonly IDomainEventSerializer? _serializer = serializers.FirstOrDefault();

    /// <summary>
    /// False for a repository whose DI scope may be LONG-LIVED - an Interactive Server circuit is one scope for as
    /// long as the page is open (D79's correction of 2026-09-21). Reads then do not track, and a write is
    /// detached once it is saved, so a re-read always answers from the database: what the RavenDB and File
    /// repositories already do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MEASURED with tracking on: the Metering, Notifications and Health pages each showed the value they FIRST read
    /// after a job had changed the row (SQLite and PostgreSQL; RavenDB was fresh), and the first click on such a
    /// page failed with a concurrency conflict for no reason the operator could see. No HTTP or SSR test can see
    /// it - a request has a scope of its own.
    /// </para>
    /// <para>
    /// The concurrency check is unchanged. EF takes a detached aggregate's <c>Version</c> as the token's ORIGINAL
    /// value when it is attached by <c>Update</c>, so the UPDATE still matches on the version that was read.
    /// </para>
    /// <para>
    /// True is the DEFAULT, and the framework seam's stores depend on it: they are built on "the store tracks what
    /// it loads, reads go through the session", and eighteen tests pin exactly that. Only the storage-blind
    /// module registration (<c>AddModuleAggregatePersistence</c>, 2-arg) turns it off.
    /// </para>
    /// </remarks>
    public bool TrackReads { get; init; } = true;

    private IQueryable<TAggregate> Aggregates =>
        TrackReads ? context.Set<TAggregate>() : context.Set<TAggregate>().AsNoTracking();

    public async Task<TAggregate?> GetByIdAsync(TId id, CancellationToken ct = default) =>
        await Aggregates.FirstOrDefaultAsync(e => e.Id.Equals(id), ct);

    public async Task<IReadOnlyList<TAggregate>> GetAllAsync(CancellationToken ct = default) =>
        await Aggregates.ToListAsync(ct);

    // Translates the predicate to a SQL WHERE and runs it in the database, honouring the context's tenant
    // query filter exactly as GetAllAsync does. Safe only for the non-string predicates the IRepository.FindAsync
    // contract documents — string case-sensitivity diverges from RavenDB where the compiler cannot see it.
    public async Task<IReadOnlyList<TAggregate>> FindAsync(
        Expression<Func<TAggregate, bool>> predicate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return await Aggregates.Where(predicate).ToListAsync(ct);
    }

    public async Task AddAsync(TAggregate aggregate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        await context.Set<TAggregate>().AddAsync(aggregate, ct);
        CaptureOutbox(aggregate);
        await SaveAsync(aggregate, ct);
        AfterPersist(aggregate);
    }

    public async Task UpdateAsync(TAggregate aggregate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        context.Set<TAggregate>().Update(aggregate);
        CaptureOutbox(aggregate);
        await SaveAsync(aggregate, ct);
        AfterPersist(aggregate);
    }

    public async Task DeleteAsync(TAggregate aggregate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        context.Set<TAggregate>().Remove(aggregate);
        // Dispatch the aggregate's domain events for the deletion the same way a save does (outbox rows commit
        // in the same transaction when enabled; otherwise the unit of work dispatches them in-process).
        CaptureOutbox(aggregate);
        await SaveAsync(aggregate, ct);
        AfterPersist(aggregate);
    }

    private async Task SaveAsync(TAggregate aggregate, CancellationToken ct)
    {
        try
        {
            var written = TrackReads ? null : context.ChangeTracker.Entries().Where(e => e.State != EntityState.Unchanged).ToList();
            await context.SaveChangesAsync(ct);
            foreach (var entry in written ?? [])
            {
                entry.State = EntityState.Detached;
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            // LEAVE THE CONTEXT USABLE. EF does not undo a failed SaveChanges: the entries it could not write
            // stay tracked as Modified/Added, so the NEXT SaveChangesAsync in this scope re-submits them and
            // fails again — and it fails while reporting the aggregate of whichever call is running, not the one
            // that actually conflicted.
            //
            // MEASURED (SaveOncePassContainmentTests). A pass updating three rows, where only the middle
            // one was stale, lost the third as well — and lost it INSIDE a per-row try/catch that existed to
            // contain exactly this. The third row's concurrency token was current; it failed only because it
            // followed a failed save on the same context. Running the same pass with a fresh scope per row let
            // it through, which is what isolates the cause to the context rather than the row.
            //
            // That makes this the right place to fix it rather than each caller: a per-row guard is the obvious
            // containment and it silently did not work, so every existing one was reporting rows as
            // conflict-skipped that had never conflicted (UsageRollupJob.RepairQuotasAsync, BL-151, is one).
            //
            // Discarding the whole pending set is correct HERE because writes are eager — Add/Update/Delete each
            // end in this method, so the only pending changes are the ones the failed call introduced, the
            // aggregate plus any outbox rows CaptureOutbox staged for it. Those outbox rows must not survive: the
            // write they announce did not happen.
            DiscardFailedWrite();

            throw new ConcurrencyConflictException(typeof(TAggregate).Name, aggregate.Id.ToString() ?? string.Empty);
        }
    }

    /// <summary>Returns the context to a clean state after a failed save, so the scope can keep working.</summary>
    private void DiscardFailedWrite()
    {
        foreach (var entry in context.ChangeTracker.Entries().Where(e => e.State != EntityState.Unchanged).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    // Add outbox rows for the aggregate's domain events to the same context, so they commit in the
    // same transaction as the aggregate.
    private void CaptureOutbox(TAggregate aggregate)
    {
        if (_serializer is null)
        {
            return;
        }

        var tenantId = (aggregate as ITenantOwned)?.TenantId.Value ?? tenantContext.CurrentTenantId?.Value;
        foreach (var domainEvent in aggregate.DomainEvents)
        {
            var (eventType, payload) = _serializer.Serialize(domainEvent);
            context.Set<OutboxMessage>().Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                EventType = eventType,
                Payload = payload,
                TenantId = tenantId,
                OccurredOnUtc = domainEvent.OccurredOn,
            });
        }
    }

    private void AfterPersist(TAggregate aggregate)
    {
        if (_serializer is not null)
        {
            aggregate.ClearDomainEvents();
        }
        else
        {
            aggregateTracker.Track(aggregate);
        }
    }
}
