using System.Runtime.CompilerServices;
using Stomme.Persistence.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Stomme.Persistence.EfCore;

/// <summary>
/// EF Core <see cref="IChangeNotifier"/>: a <see cref="SaveChangesInterceptor"/> that snapshots the
/// aggregate-root entries entering a save and publishes them as <see cref="EntityChange"/>s only after
/// the save commits — a failed save publishes nothing. <see cref="EntityChange.EntityType"/> is the
/// aggregate's CLR type name (the same singular convention the RavenDB and in-memory feeds use, so
/// consumers behave identically), the id is the primary-key value, and the tenant is stamped from the
/// entity when it is <see cref="ITenantOwned"/> (null otherwise, e.g. system-scoped aggregates).
/// Non-aggregate rows (outbox messages, owned rows) are filtered out. Registered as a singleton and
/// attached to every store context by
/// <see cref="EfCorePersistenceExtensions.AddEfCorePersistence{TContext}"/>.
/// <para>
/// <b>Node-local semantics:</b> unlike the RavenDB Changes API (cluster-wide — every node observes any
/// node's write), this feed only observes saves made through <em>this process's</em> contexts. On a
/// multi-node EF deployment the host wires the change-feed fan-out on top (the persistence building
/// block's <c>EntityChangeBusRelay</c> publishes each change as an <c>EntityChangedEvent</c> and remote
/// nodes replay it through their <c>RemoteChangeNotifier</c> loopback), so consumers still see every
/// node's writes when a pub/sub-capable bus transport is configured.
/// </para>
/// </summary>
public sealed class EfCoreChangeNotifier : SaveChangesInterceptor, IChangeNotifier
{
    // Changes captured when a save begins, keyed per context so concurrent saves on different contexts
    // never mix. Entries are removed on publish/failure; the weak table never roots a context.
    private readonly ConditionalWeakTable<DbContext, List<EntityChange>> _pending = new();

    /// <inheritdoc />
    public event Action<EntityChange>? Changed;

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Publish(eventData.Context);
        return base.SavedChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Publish(eventData.Context);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    /// <inheritdoc />
    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        Discard(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    /// <inheritdoc />
    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    // Snapshot the pending aggregate changes before the save resets entry states (Added -> Unchanged,
    // Deleted -> Detached); published only if the save completes.
    private void Capture(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        _pending.Remove(context);
        List<EntityChange>? changes = null;
        foreach (var entry in context.ChangeTracker.Entries())
        {
            var kind = entry.State switch
            {
                EntityState.Added => ChangeKind.Created,
                EntityState.Modified => ChangeKind.Updated,
                EntityState.Deleted => ChangeKind.Deleted,
                _ => (ChangeKind?)null,
            };

            if (kind is null || !StommeDbContext.IsAggregateRoot(entry.Metadata.ClrType))
            {
                continue;
            }

            if (ToChange(entry, kind.Value) is { } change)
            {
                (changes ??= []).Add(change);
            }
        }

        if (changes is not null)
        {
            _pending.Add(context, changes);
        }
    }

    private void Publish(DbContext? context)
    {
        if (context is null || !_pending.TryGetValue(context, out var changes))
        {
            return;
        }

        _pending.Remove(context);
        foreach (var change in changes)
        {
            Changed?.Invoke(change);
        }
    }

    private void Discard(DbContext? context)
    {
        if (context is not null)
        {
            _pending.Remove(context);
        }
    }

    private static EntityChange? ToChange(EntityEntry entry, ChangeKind kind)
    {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null || key.Properties.Count != 1)
        {
            return null; // aggregates carry a single-property key; anything else is not addressable
        }

        var id = entry.Property(key.Properties[0].Name).CurrentValue?.ToString();
        if (string.IsNullOrEmpty(id))
        {
            return null;
        }

        return new EntityChange(
            entry.Metadata.ClrType.Name,
            id,
            (entry.Entity as ITenantOwned)?.TenantId.ToString(),
            kind);
    }
}
