namespace Stomme.Infrastructure.EventBus;

/// <summary>
/// A bus message captured while the transport was unavailable. <see cref="Replay"/> closes over
/// the inner <see cref="RebusEventBus"/> and the original message so it can be re-dispatched by
/// <see cref="BusTransportFlusher"/> without resolving any additional services at flush time.
/// <see cref="RebusEventBus"/> wraps a singleton <see cref="Rebus.Bus.IBus"/> — holding a closure
/// reference post-scope is safe because it has no disposable state of its own.
/// </summary>
internal sealed record BusBufferedMessage(Func<CancellationToken, Task> Replay, string Description);
