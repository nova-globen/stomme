namespace Stomme.Persistence.Abstractions;

/// <summary>
/// Thrown when an optimistic-concurrency check fails (a stale write). The conflict is surfaced to
/// the caller for retry or merge — the contract Collaboration's merge logic builds on.
/// </summary>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string entityType, string entityId)
        : base($"Optimistic concurrency conflict on {entityType} '{entityId}'.")
    {
        EntityType = entityType;
        EntityId = entityId;
    }

    public ConcurrencyConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
        EntityType = string.Empty;
        EntityId = string.Empty;
    }

    public string EntityType { get; }

    public string EntityId { get; }
}
