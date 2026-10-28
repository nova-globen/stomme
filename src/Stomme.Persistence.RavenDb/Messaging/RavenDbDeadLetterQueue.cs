using System.Globalization;
using Stomme.EventBus;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Rebus.Messages;

namespace Stomme.Persistence.RavenDb.Messaging;

/// <summary>
/// RavenDB-backed <see cref="IDeadLetterQueue"/>. Dead-lettered messages are just queue documents whose
/// <see cref="RavenTransportMessage.Queue"/> is the error queue, so listing, retrying (return to the
/// input queue), and discarding are document queries/updates.
/// </summary>
internal sealed class RavenDbDeadLetterQueue(IDocumentStore store, string inputQueue, string errorQueue) : IDeadLetterQueue
{
    public async Task<IReadOnlyList<DeadLetterMessage>> ListAsync(int max = 100, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var messages = await session.Query<RavenTransportMessage>()
            .Customize(customize => customize.WaitForNonStaleResults())
            .Where(m => m.Queue == errorQueue)
            .OrderBy(m => m.VisibleUtc)
            .Take(Math.Clamp(max, 1, 1000))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return messages.Select(Project).ToArray();
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        return await session.Query<RavenTransportMessage>()
            .Customize(customize => customize.WaitForNonStaleResults())
            .CountAsync(m => m.Queue == errorQueue, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> RetryAsync(string id, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var message = await session.LoadAsync<RavenTransportMessage>(id, cancellationToken).ConfigureAwait(false);
        if (message is null || message.Queue != errorQueue)
        {
            return false;
        }

        message.Queue = inputQueue;
        message.VisibleUtc = DateTime.UtcNow;
        message.LeasedUntilTicks = 0;
        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> DiscardAsync(string id, CancellationToken cancellationToken = default)
    {
        using var session = store.OpenAsyncSession();
        var message = await session.LoadAsync<RavenTransportMessage>(id, cancellationToken).ConfigureAwait(false);
        if (message is null || message.Queue != errorQueue)
        {
            return false;
        }

        session.Delete(message);
        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static DeadLetterMessage Project(RavenTransportMessage message)
    {
        message.Headers.TryGetValue(Headers.Type, out var type);
        message.Headers.TryGetValue(Headers.ErrorDetails, out var error);

        // VisibleUtc is stamped by RavenDbTransport.Send when the message is moved to the error queue (and again on a
        // re-dead-letter after a retry), so it is the dead-letter time; SentTime is the original send time.
        var visible = new DateTimeOffset(DateTime.SpecifyKind(message.VisibleUtc, DateTimeKind.Utc));
        var enqueued = visible;
        if (message.Headers.TryGetValue(Headers.SentTime, out var sentTime)
            && DateTimeOffset.TryParse(sentTime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var sent))
        {
            enqueued = sent;
        }

        return new DeadLetterMessage(message.Id, type, error, enqueued, message.Headers, visible);
    }
}
