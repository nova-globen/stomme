namespace Stomme.SharedKernel.Domain;

/// <summary>
/// Marker interface for domain events.
/// Domain events represent something meaningful that happened in the domain.
/// </summary>
public interface IDomainEvent
{
    /// <summary>
    /// When the event occurred.
    /// </summary>
    DateTimeOffset OccurredOn { get; }
}

/// <summary>
/// Base record for domain events providing a default timestamp.
/// </summary>
public abstract record DomainEvent : IDomainEvent
{
    public DateTimeOffset OccurredOn { get; init; } = DateTimeOffset.UtcNow;
}
