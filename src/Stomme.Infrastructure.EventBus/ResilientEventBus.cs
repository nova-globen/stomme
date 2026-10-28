using Stomme.EventBus;
using Microsoft.Extensions.Logging;

namespace Stomme.Infrastructure.EventBus;

/// <summary>
/// Fault-tolerant decorator for <see cref="IEventBus"/>. When the underlying transport
/// (RabbitMQ, ASB) is unreachable, messages are queued in a bounded <see cref="BusTransportBuffer"/>
/// and replayed by <see cref="BusTransportFlusher"/> once connectivity resumes. When the buffer is
/// full, the caller receives an <see cref="InvalidOperationException"/> so the operation is not
/// silently dropped. Registered automatically by
/// <see cref="MessagingServiceExtensions.AddMessaging"/>.
/// </summary>
internal sealed class ResilientEventBus(
    IEventBus inner,
    BusTransportBuffer buffer,
    ILogger<ResilientEventBus> logger) : IEventBus
{
    public async Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : class
    {
        try { await inner.PublishAsync(@event, ct).ConfigureAwait(false); }
        catch (Exception ex) { Buffer(ex, ct2 => inner.PublishAsync(@event, ct2), $"Publish<{typeof(TEvent).Name}>"); }
    }

    public async Task PublishAsync<TEvent>(
        TEvent @event,
        IDictionary<string, string>? headers = null,
        CancellationToken ct = default)
        where TEvent : class
    {
        try { await inner.PublishAsync(@event, headers, ct).ConfigureAwait(false); }
        catch (Exception ex) { Buffer(ex, ct2 => inner.PublishAsync(@event, headers, ct2), $"Publish<{typeof(TEvent).Name}>"); }
    }

    public async Task SendAsync<TCommand>(TCommand command, CancellationToken ct = default)
        where TCommand : class
    {
        try { await inner.SendAsync(command, ct).ConfigureAwait(false); }
        catch (Exception ex) { Buffer(ex, ct2 => inner.SendAsync(command, ct2), $"Send<{typeof(TCommand).Name}>"); }
    }

    public async Task SendAsync<TCommand>(
        TCommand command,
        string destinationQueue,
        CancellationToken ct = default)
        where TCommand : class
    {
        try { await inner.SendAsync(command, destinationQueue, ct).ConfigureAwait(false); }
        catch (Exception ex) { Buffer(ex, ct2 => inner.SendAsync(command, destinationQueue, ct2), $"Send<{typeof(TCommand).Name}>→{destinationQueue}"); }
    }

    public async Task SendLocalAsync<TCommand>(TCommand command, CancellationToken ct = default)
        where TCommand : class
    {
        try { await inner.SendLocalAsync(command, ct).ConfigureAwait(false); }
        catch (Exception ex) { Buffer(ex, ct2 => inner.SendLocalAsync(command, ct2), $"SendLocal<{typeof(TCommand).Name}>"); }
    }

    public async Task DeferAsync<TMessage>(TimeSpan delay, TMessage message, CancellationToken ct = default)
        where TMessage : class
    {
        try { await inner.DeferAsync(delay, message, ct).ConfigureAwait(false); }
        catch (Exception ex) { Buffer(ex, ct2 => inner.DeferAsync(delay, message, ct2), $"Defer<{typeof(TMessage).Name}>@{delay}"); }
    }

    private void Buffer(Exception transportEx, Func<CancellationToken, Task> replay, string description)
    {
        if (!buffer.TryEnqueue(new BusBufferedMessage(replay, description)))
        {
            throw new InvalidOperationException(
                $"Bus transport is unavailable and the in-memory buffer is full (capacity {buffer.Capacity}). " +
                $"Message dropped: {description}.",
                transportEx);
        }
        logger.LogWarning(transportEx,
            "Bus transport unavailable; message buffered in-memory ({Count}/{Capacity}): {Description}.",
            buffer.Count, buffer.Capacity, description);
    }
}
