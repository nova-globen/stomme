using Stomme.EventBus;
using Rebus.Bus;

namespace Stomme.Infrastructure.EventBus;

/// <summary>
/// Rebus-backed implementation of <see cref="IEventBus"/> for inter-bounded-context communication.
/// </summary>
public sealed class RebusEventBus(IBus bus) : IEventBus
{
    public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : class
    {
        ct.ThrowIfCancellationRequested();
        return bus.Publish(@event);
    }

    public Task PublishAsync<TEvent>(
        TEvent @event,
        IDictionary<string, string>? headers = null,
        CancellationToken ct = default)
        where TEvent : class
    {
        ct.ThrowIfCancellationRequested();
        return headers is null
            ? bus.Publish(@event)
            : bus.Publish(@event, headers);
    }

    public Task SendAsync<TCommand>(TCommand command, CancellationToken ct = default)
        where TCommand : class
    {
        ct.ThrowIfCancellationRequested();
        return bus.Send(command);
    }

    public Task SendAsync<TCommand>(
        TCommand command,
        string destinationQueue,
        CancellationToken ct = default)
        where TCommand : class
    {
        ct.ThrowIfCancellationRequested();
        return bus.Advanced.Routing.Send(destinationQueue, command);
    }

    public Task SendLocalAsync<TCommand>(TCommand command, CancellationToken ct = default)
        where TCommand : class
    {
        ct.ThrowIfCancellationRequested();
        return bus.SendLocal(command);
    }

    public Task DeferAsync<TMessage>(
        TimeSpan delay,
        TMessage message,
        CancellationToken ct = default)
        where TMessage : class
    {
        ct.ThrowIfCancellationRequested();
        return bus.Defer(delay, message);
    }
}
