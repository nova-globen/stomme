using Stomme.Persistence.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Changes;

namespace Stomme.Persistence.RavenDb;

/// <summary>
/// RavenDB <see cref="IChangeNotifier"/> over the Changes API: subscribes to all document changes and
/// republishes them as <see cref="EntityChange"/> for Cache (invalidation) and Collaboration (edit
/// sync). Infrastructure collections (outbox, transport, subscriptions) are filtered out. The change
/// feed carries the collection + id but not the tenant, so <see cref="EntityChange.TenantId"/> is null;
/// consumers that need tenant scoping derive it from the key. Registered as a singleton (one feed per
/// store); the subscription opens on first resolution.
/// </summary>
public sealed class RavenChangeNotifier : IChangeNotifier, IObserver<DocumentChange>, IDisposable
{
    private static readonly HashSet<string> InfrastructureCollections =
        new(StringComparer.OrdinalIgnoreCase) { "OutboxMessages", "RavenTransportMessages", "RavenSubscriptions" };

    private readonly IChangesObservable<DocumentChange> _observable;
    private readonly IDisposable _subscription;

    public event Action<EntityChange>? Changed;

    public RavenChangeNotifier(IDocumentStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _observable = store.Changes().ForAllDocuments();
        _subscription = _observable.Subscribe(this);
    }

    /// <summary>Awaits the server subscription so a caller can observe changes deterministically (used by tests).</summary>
    public Task EnsureSubscribedAsync() => _observable.EnsureSubscribedNow();

    public void OnNext(DocumentChange change)
    {
        if (change.Type is not (DocumentChangeTypes.Put or DocumentChangeTypes.Delete))
        {
            return;
        }

        var collection = change.CollectionName ?? string.Empty;
        if (collection.Length == 0 || collection.StartsWith('@') || InfrastructureCollections.Contains(collection))
        {
            return;
        }

        var kind = change.Type == DocumentChangeTypes.Delete ? ChangeKind.Deleted : ChangeKind.Updated;

        // Derive the entity type and id from the document id ("{Type}/{id}") so they match the
        // repository's DocId convention (singular type), rather than RavenDB's pluralized collection name.
        var id = change.Id ?? string.Empty;
        var separator = id.IndexOf('/');
        var entityType = separator > 0 ? id[..separator] : collection;
        var entityId = separator >= 0 && separator < id.Length - 1 ? id[(separator + 1)..] : id;

        Changed?.Invoke(new EntityChange(entityType, entityId, TenantId: null, kind));
    }

    public void OnError(Exception error)
    {
        // The Changes API reconnects on transient errors; nothing to surface to change consumers.
    }

    public void OnCompleted()
    {
    }

    public void Dispose()
    {
        _subscription.Dispose();
        (_observable as IDisposable)?.Dispose();
    }
}
