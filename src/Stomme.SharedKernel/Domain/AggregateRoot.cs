namespace Stomme.SharedKernel.Domain;

/// <summary>
/// Base class for aggregate roots. Aggregates are the transactional consistency boundary.
/// Only aggregate roots should be persisted directly via repositories.
/// </summary>
public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : notnull
{
    /// <summary>
    /// Concurrency token for optimistic concurrency control.
    /// </summary>
    public uint Version { get; protected set; }
}
