namespace Stomme.EventBus;

/// <summary>
/// Abstraction over the event bus for publishing integration events across bounded contexts.
/// Transport implementation is provided by consuming infrastructure/host projects. This is the
/// cross-context, async integration bus — distinct from the in-process CQRS mediator.
/// </summary>
public interface IEventBus
{
    Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : class;

    Task PublishAsync<TEvent>(TEvent @event, IDictionary<string, string>? headers = null, CancellationToken ct = default)
        where TEvent : class;

    Task SendAsync<TCommand>(TCommand command, CancellationToken ct = default)
        where TCommand : class;

    Task SendAsync<TCommand>(TCommand command, string destinationQueue, CancellationToken ct = default)
        where TCommand : class;

    /// <summary>
    /// Sends a command to the bus's own input queue, where an in-process consumer handles it. The reliable
    /// hand-off for a point-to-point command whose handler runs in the same host (no endpoint-routing config and
    /// no queue name needed), e.g. a producer module handing work to a consumer module in the modular monolith.
    /// </summary>
    Task SendLocalAsync<TCommand>(TCommand command, CancellationToken ct = default)
        where TCommand : class;

    Task DeferAsync<TMessage>(TimeSpan delay, TMessage message, CancellationToken ct = default)
        where TMessage : class;
}
