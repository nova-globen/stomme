namespace Stomme.Persistence.Abstractions;

/// <summary>The kind of change an entity underwent.</summary>
public enum ChangeKind
{
    Created,
    Updated,
    Deleted,
}

/// <summary>
/// A tracked persistence change, consumed by Collaboration (real-time edit sync) and Cache
/// (invalidation). Carries enough to route the change without loading the entity.
/// </summary>
public sealed record EntityChange(string EntityType, string EntityId, string? TenantId, ChangeKind Kind);

/// <summary>
/// Publishes <see cref="EntityChange"/> notifications as aggregates are persisted. For RavenDB this
/// maps to the Changes API; relational stores use a polling/CDC adapter.
/// </summary>
public interface IChangeNotifier
{
    /// <summary>Raised after a change is committed.</summary>
    event Action<EntityChange>? Changed;
}
