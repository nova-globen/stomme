using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Rebus.Subscriptions;

namespace Stomme.Persistence.RavenDb.Messaging;

/// <summary>
/// Centralized Rebus <see cref="ISubscriptionStorage"/> over RavenDB: a subscription is a document
/// keyed by topic + subscriber, so a publisher resolves the current subscribers for a topic at
/// publish time. Centralized means subscribers register their own address here rather than asking the
/// owning publisher.
/// </summary>
internal sealed class RavenDbSubscriptionStorage(IDocumentStore store) : ISubscriptionStorage
{
    public bool IsCentralized => true;

    public async Task<IReadOnlyList<string>> GetSubscriberAddresses(string topic)
    {
        using var session = store.OpenAsyncSession();
        var subscriptions = await session.Query<RavenSubscription>()
            .Customize(customize => customize.WaitForNonStaleResults())
            .Where(subscription => subscription.Topic == topic)
            .ToListAsync()
            .ConfigureAwait(false);

        return subscriptions.Select(subscription => subscription.Address).Distinct().ToArray();
    }

    public async Task RegisterSubscriber(string topic, string subscriberAddress)
    {
        using var session = store.OpenAsyncSession();
        var id = BuildId(topic, subscriberAddress);
        await session.StoreAsync(new RavenSubscription { Id = id, Topic = topic, Address = subscriberAddress }, id)
            .ConfigureAwait(false);
        await session.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task UnregisterSubscriber(string topic, string subscriberAddress)
    {
        using var session = store.OpenAsyncSession();
        session.Delete(BuildId(topic, subscriberAddress));
        await session.SaveChangesAsync().ConfigureAwait(false);
    }

    private static string BuildId(string topic, string subscriberAddress) =>
        $"RavenSubscriptions/{Uri.EscapeDataString(topic)}/{Uri.EscapeDataString(subscriberAddress)}";
}
