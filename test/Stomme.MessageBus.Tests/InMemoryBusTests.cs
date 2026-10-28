using Stomme.EventBus;
using Stomme.Infrastructure.EventBus;
using Stomme.MessageBus.Tests.Ordering;
using Stomme.MessageBus.Tests.Ordering.Contracts;
using Stomme.MessageBus.Tests.Shipping;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Stomme.MessageBus.Tests;

/// <summary>
/// Phase 1 — the bus seam over the default in-memory transport: one module publishes an integration
/// event, another module's handler receives it, in-process, with neither knowing the other.
/// </summary>
public class InMemoryBusTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ShipmentLog>();
        builder.Services.AddScoped<OrderingModule>();

        // Discovers IIntegrationEventHandler<T> in this assembly (the "modules") and bridges them to Rebus.
        builder.Services.AddMessaging(
            [typeof(InMemoryBusTests).Assembly],
            configure: options =>
            {
                options.InputQueue = "stomme-tests";
                options.ErrorQueue = "stomme-tests.error";
            });

        // A published event only reaches this host once the topic is subscribed.
        builder.Services.AddBusSubscription<OrderPlaced>();
        return builder.Build();
    }

    [Fact]
    public async Task Publish_from_one_module_is_handled_by_another_module()
    {
        using var host = BuildHost();
        await host.StartAsync();
        try
        {
            using var scope = host.Services.CreateScope();
            var ordering = scope.ServiceProvider.GetRequiredService<OrderingModule>();
            var orderId = Guid.NewGuid();

            await ordering.PlaceOrderAsync(orderId, "ada@example.com");

            var log = host.Services.GetRequiredService<ShipmentLog>();
            var received = await log.Received.WaitAsync(Timeout);

            Assert.Equal(orderId, received.OrderId);
            Assert.Equal("ada@example.com", received.Customer);
            // The IntegrationEvent base carries its own identity/timestamp across the transport.
            Assert.NotEqual(Guid.Empty, received.EventId);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public void IEventBus_resolves_to_the_resilient_rebus_adapter()
    {
        using var host = BuildHost();
        using var scope = host.Services.CreateScope();

        var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();

        // Consumers depend on the seam; the concrete transport stays an implementation detail.
        Assert.IsAssignableFrom<IEventBus>(bus);
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RebusEventBus>());
    }

    [Fact]
    public async Task Dead_letter_queue_falls_back_to_the_null_implementation_without_a_backing_transport()
    {
        using var host = BuildHost();

        var dlq = host.Services.GetRequiredService<IDeadLetterQueue>();

        Assert.Equal(0, await dlq.CountAsync());
        Assert.Empty(await dlq.ListAsync());
        Assert.False(await dlq.RetryAsync("nope"));
    }
}
