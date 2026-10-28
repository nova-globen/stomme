using System.Collections.Concurrent;
using Stomme.Persistence.Abstractions;

namespace Stomme.Persistence;

/// <summary>
/// In-process <see cref="IOutboxStore"/>. Not durable, so it does not provide real transactional
/// guarantees; it exists to exercise the outbox processor in tests and for hosts that want the outbox
/// dispatch model without a database. Durable hosts use the RavenDB or EF Core outbox stores.
/// </summary>
public sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly ConcurrentDictionary<Guid, OutboxMessage> _messages = new();

    /// <summary>Captures a message (the durable providers do this inside the aggregate's transaction).</summary>
    public void Enqueue(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _messages[message.Id] = message;
    }

    public Task<IReadOnlyList<OutboxMessage>> GetUnprocessedBatchAsync(int batchSize, int maxAttempts, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult<IReadOnlyList<OutboxMessage>>(
            _messages.Values
                .Where(m => m.ProcessedOnUtc is null && m.AttemptCount < maxAttempts
                    && (m.NextAttemptOnUtc == null || m.NextAttemptOnUtc <= now))
                .OrderBy(m => m.OccurredOnUtc)
                .Take(batchSize)
                .ToList());
    }

    public Task MarkProcessedAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        message.ProcessedOnUtc = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(OutboxMessage message, string error, CancellationToken cancellationToken = default)
    {
        message.AttemptCount++;
        message.LastError = error;
        return Task.CompletedTask;
    }
}
