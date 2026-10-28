namespace Stomme.Persistence.Abstractions;

/// <summary>
/// Provider-specific persistence for <see cref="OutboxMessage"/>. Each store backs a relational,
/// document, or in-memory provider; the outbox processor reads pending messages and marks them
/// processed or failed through this contract. Writes that capture messages happen inside the
/// aggregate's own transaction and are therefore done by the provider's repository/context, not here.
/// </summary>
public interface IOutboxStore
{
    /// <summary>
    /// Returns up to <paramref name="batchSize"/> pending messages, oldest first, excluding dead
    /// letters (those whose attempt count has reached <paramref name="maxAttempts"/>).
    /// </summary>
    Task<IReadOnlyList<OutboxMessage>> GetUnprocessedBatchAsync(int batchSize, int maxAttempts, CancellationToken cancellationToken = default);

    /// <summary>Marks a message as successfully dispatched.</summary>
    Task MarkProcessedAsync(OutboxMessage message, CancellationToken cancellationToken = default);

    /// <summary>Records a failed dispatch attempt (increments the attempt count, keeps it pending).</summary>
    Task MarkFailedAsync(OutboxMessage message, string error, CancellationToken cancellationToken = default);
}
