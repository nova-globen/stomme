using System.Globalization;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using Rebus.Messages;
using Rebus.Transport;

namespace Stomme.Persistence.RavenDb.Messaging;

/// <summary>
/// A custom Rebus <see cref="ITransport"/> backed by RavenDB — there is no first-party Rebus RavenDB
/// transport, so this is implemented over Stomme's RavenDB store. Messages are queue documents;
/// sends are buffered and flushed on the ambient transaction's commit; receives lease a message with
/// optimistic concurrency (only one worker wins) and delete on ack / release the lease on nack. A lease
/// auto-expires so a crashed receiver's message is redelivered (at-least-once).
/// <see cref="Headers.DeferredUntil"/> is honored natively via the message's visibility time.
/// </summary>
internal sealed class RavenDbTransport(IDocumentStore store, string inputQueueAddress, TimeSpan leaseTime) : ITransport
{
    private const string OutgoingKey = "stomme-ravendb-outgoing";

    public string Address => inputQueueAddress;

    // Queues are logical (a field on the document); nothing to provision.
    public void CreateQueue(string address)
    {
    }

    public Task Send(string destinationAddress, TransportMessage message, ITransactionContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationAddress);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);

        var headers = new Dictionary<string, string>(message.Headers);
        var visibleUtc = DateTime.UtcNow;

        // Native deferral: if Rebus marked the message deferred, hold it invisible until then and
        // strip the defer headers so it is not deferred again when received.
        if (headers.TryGetValue(Headers.DeferredUntil, out var deferUntil)
            && DateTimeOffset.TryParse(deferUntil, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until))
        {
            visibleUtc = until.UtcDateTime;
            headers.Remove(Headers.DeferredUntil);
            headers.Remove(Headers.DeferredRecipient);
        }

        var document = new RavenTransportMessage
        {
            Id = "RavenTransportMessages/" + Guid.NewGuid().ToString("n"),
            Queue = destinationAddress,
            Headers = headers,
            Body = message.Body,
            VisibleUtc = visibleUtc,
            LeasedUntilTicks = 0,
        };

        // Buffer outgoing messages and flush them when the ambient transaction commits, so a message
        // is only enqueued if the unit of work that produced it succeeds.
        var outgoing = context.GetOrAdd(OutgoingKey, () =>
        {
            var buffer = new List<RavenTransportMessage>();
            context.OnCommit(async _ =>
            {
                using var session = store.OpenAsyncSession();
                foreach (var pending in buffer)
                {
                    await session.StoreAsync(pending, pending.Id).ConfigureAwait(false);
                }

                await session.SaveChangesAsync().ConfigureAwait(false);
            });
            return buffer;
        });

        outgoing.Add(document);
        return Task.CompletedTask;
    }

    public async Task<TransportMessage> Receive(ITransactionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var queryNow = DateTime.UtcNow;
        var nowTicks = queryNow.Ticks;
        List<string> candidateIds;
        using (var query = store.OpenAsyncSession())
        {
            candidateIds = await query.Query<RavenTransportMessage>()
                .Customize(customize => customize.WaitForNonStaleResults())
                .Where(m => m.Queue == inputQueueAddress && m.VisibleUtc <= queryNow && m.LeasedUntilTicks < nowTicks)
                .OrderBy(m => m.VisibleUtc)
                .Take(10)
                .Select(m => m.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var id in candidateIds)
        {
            using var lease = store.OpenAsyncSession();
            lease.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.WritesAndReads;

            var message = await lease.LoadAsync<RavenTransportMessage>(id, cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                continue;
            }

            var now = DateTime.UtcNow;
            if (message.VisibleUtc > now || message.LeasedUntilTicks >= now.Ticks)
            {
                continue; // became deferred or another worker leased it between the query and now
            }

            message.LeasedUntilTicks = now.Add(leaseTime).Ticks;
            try
            {
                await lease.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ConcurrencyException)
            {
                continue; // lost the race for this message; try the next candidate
            }

            // Delete on successful handling; release the lease (redeliver) on failure.
            context.OnAck(_ => DeleteAsync(id));
            context.OnNack(_ => ReleaseAsync(id));

            return new TransportMessage(new Dictionary<string, string>(message.Headers), message.Body);
        }

        return null!; // Rebus treats a null return as "no message available".
    }

    private async Task DeleteAsync(string id)
    {
        using var session = store.OpenAsyncSession();
        session.Delete(id);
        await session.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task ReleaseAsync(string id)
    {
        using var session = store.OpenAsyncSession();
        var message = await session.LoadAsync<RavenTransportMessage>(id).ConfigureAwait(false);
        if (message is not null)
        {
            message.LeasedUntilTicks = 0;
            await session.SaveChangesAsync().ConfigureAwait(false);
        }
    }
}
