namespace Stomme.EventBus;

/// <summary>
/// Handler for integration events that cross bounded context boundaries. Modules depend only on this
/// abstraction; the Rebus adapter bridges it to the transport. Delivery is at-least-once, so handlers
/// must be idempotent.
/// </summary>
public interface IIntegrationEventHandler<in TEvent>
    where TEvent : class
{
    Task HandleAsync(TEvent @event, CancellationToken ct = default);
}
