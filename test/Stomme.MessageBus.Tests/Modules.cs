using Stomme.EventBus;

// Two stand-in modules exercising the cross-context integration bus. "Ordering" owns the contract and
// publishes; "Shipping" consumes it. Neither references the other's internals — only the shared
// integration-event contract and the IEventBus/IIntegrationEventHandler abstractions, which is the
// whole point of the seam.

namespace Stomme.MessageBus.Tests.Ordering.Contracts
{
    /// <summary>Published by the Ordering module when an order is placed. Cross-context contract.</summary>
    public sealed record OrderPlaced(Guid OrderId, string Customer) : IntegrationEvent;
}

namespace Stomme.MessageBus.Tests.Ordering
{
    using Stomme.MessageBus.Tests.Ordering.Contracts;

    /// <summary>The Ordering module's publish-side entry point; depends only on <see cref="IEventBus"/>.</summary>
    public sealed class OrderingModule(IEventBus bus)
    {
        public Task PlaceOrderAsync(Guid orderId, string customer, CancellationToken ct = default) =>
            bus.PublishAsync(new OrderPlaced(orderId, customer), ct);
    }
}

namespace Stomme.MessageBus.Tests.Shipping
{
    using Stomme.MessageBus.Tests.Ordering.Contracts;

    /// <summary>Records what the Shipping module received, so tests can await delivery deterministically.</summary>
    public sealed class ShipmentLog
    {
        private readonly TaskCompletionSource<OrderPlaced> _received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OrderPlaced> Received => _received.Task;

        public void Record(OrderPlaced e) => _received.TrySetResult(e);
    }

    /// <summary>
    /// The Shipping module's reaction to an Ordering event. Delivery is at-least-once, so a real handler
    /// must be idempotent; this one is (TrySetResult ignores repeats).
    /// </summary>
    public sealed class OrderPlacedConsumer(ShipmentLog log) : IIntegrationEventHandler<OrderPlaced>
    {
        public Task HandleAsync(OrderPlaced @event, CancellationToken ct = default)
        {
            log.Record(@event);
            return Task.CompletedTask;
        }
    }
}
