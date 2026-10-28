namespace Stomme.EventBus;

/// <summary>
/// Base record for integration events that cross bounded context boundaries via the event bus.
/// Integration events are the contract between modules; they are distinct from domain events, which
/// are internal to a bounded context and bridge to integration events through the outbox.
/// </summary>
public abstract record IntegrationEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredOn { get; init; } = DateTimeOffset.UtcNow;
    public string? CorrelationId { get; init; }
}
