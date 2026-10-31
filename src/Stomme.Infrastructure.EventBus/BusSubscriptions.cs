using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Rebus.Bus;

namespace Stomme.Infrastructure.EventBus;

/// <summary>
/// A pub/sub topic this host subscribes to at startup. Point-to-point sends need no subscription; a
/// <em>published</em> event only reaches this host's input queue once the event type is subscribed, so
/// components that consume published events register one of these via
/// <see cref="BusSubscriptionServiceExtensions.AddBusSubscription{TEvent}"/>.
/// </summary>
public sealed record BusTopicSubscription(Type EventType);

/// <summary>
/// Registers the host's <see cref="BusTopicSubscription"/>s with Rebus when the host starts: with a
/// broker transport (RabbitMQ/Azure Service Bus) this binds the input queue to the topic natively; with
/// the RavenDB transport it writes the centralized subscription document; with the default in-memory
/// transport it binds against that transport's own subscription storage. Subscriptions are durable and
/// intentionally not removed on stop — a restarting node must not miss events published while it was
/// down (delivery is at-least-once; handlers are idempotent). With messaging off there is no bus and nothing to subscribe:
/// the initializer does nothing, so a module that subscribes unconditionally does not stop the host from starting (BL-251,
/// BL-239's wE1-3: it required <see cref="IBus"/>, which only messaging registers, and container validation refused the host).
/// </summary>
public sealed class BusSubscriptionInitializer(IServiceProvider services, IEnumerable<BusTopicSubscription> subscriptions) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService(typeof(IBus)) is not IBus bus)
        {
            return;
        }

        foreach (var eventType in subscriptions.Select(s => s.EventType).Distinct())
        {
            await bus.Subscribe(eventType).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Registration sugar for <see cref="BusTopicSubscription"/>.</summary>
public static class BusSubscriptionServiceExtensions
{
    /// <summary>
    /// Subscribes this host to the published <typeparamref name="TEvent"/> topic at startup so the
    /// event's <c>IIntegrationEventHandler</c>s actually receive publishes. Requires a transport with
    /// pub/sub support: a broker natively, RavenDB via its centralized subscription storage, or the
    /// default in-memory transport, which registers its own centralized subscription storage
    /// (single-node only).
    /// </summary>
    public static IServiceCollection AddBusSubscription<TEvent>(this IServiceCollection services)
        where TEvent : class
    {
        services.AddSingleton(new BusTopicSubscription(typeof(TEvent)));
        services.AddHostedService<BusSubscriptionInitializer>();
        return services;
    }
}
