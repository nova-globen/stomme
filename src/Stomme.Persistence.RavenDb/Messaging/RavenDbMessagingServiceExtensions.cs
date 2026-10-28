using Stomme.EventBus;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Rebus.Config;
using Rebus.Subscriptions;
using Rebus.Transport;

namespace Stomme.Persistence.RavenDb.Messaging;

/// <summary>
/// Wires the RavenDB-backed Rebus transport (and its centralized subscription storage) into a Rebus
/// configuration. The host selects this transport when RavenDB is the active store; otherwise the
/// in-memory transport (or another configured transport) is used.
/// </summary>
public static class RavenDbTransportConfigurationExtensions
{
    /// <summary>
    /// Uses RavenDB as the Rebus transport, receiving on <paramref name="inputQueueAddress"/>, and
    /// registers RavenDB-backed centralized subscription storage so publish/subscribe works.
    /// </summary>
    public static void UseRavenDb(
        this StandardConfigurer<ITransport> configurer,
        IDocumentStore store,
        string inputQueueAddress,
        TimeSpan? leaseTime = null)
    {
        ArgumentNullException.ThrowIfNull(configurer);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputQueueAddress);

        var lease = leaseTime ?? TimeSpan.FromMinutes(5);

        configurer.Register(_ => new RavenDbTransport(store, inputQueueAddress, lease), "Stomme RavenDB transport");
        configurer.OtherService<ISubscriptionStorage>()
            .Register(_ => new RavenDbSubscriptionStorage(store), "Stomme RavenDB subscription storage");
    }
}

/// <summary>DI registration for RavenDB-backed messaging admin services.</summary>
public static class RavenDbMessagingServiceExtensions
{
    /// <summary>
    /// Registers the RavenDB-backed <see cref="IDeadLetterQueue"/> over the transport's queue
    /// documents, resolving the singleton <see cref="IDocumentStore"/> at runtime. Overrides the
    /// null fallback that <c>AddMessaging</c> registers.
    /// </summary>
    public static IServiceCollection AddRavenDbDeadLetterQueue(
        this IServiceCollection services, string inputQueue, string errorQueue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputQueue);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorQueue);

        services.AddSingleton<IDeadLetterQueue>(provider =>
            new RavenDbDeadLetterQueue(provider.GetRequiredService<IDocumentStore>(), inputQueue, errorQueue));
        return services;
    }
}
