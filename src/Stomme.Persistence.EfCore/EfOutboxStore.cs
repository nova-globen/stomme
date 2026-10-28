using Stomme.Persistence.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Stomme.Persistence.EfCore;

/// <summary>
/// EF Core <see cref="IOutboxStore"/> over the shared <c>OutboxMessages</c> table. Outbox rows are
/// written into the aggregate's transaction by <see cref="EfRepository{TAggregate, TId}"/>; this store
/// backs the processor's reads and status updates. <see cref="OutboxMessage"/> is not tenant-owned, so
/// no query filter applies and the processor sees every tenant's pending messages.
/// </summary>
internal sealed class EfOutboxStore(DbContext context) : IOutboxStore
{
    public async Task<IReadOnlyList<OutboxMessage>> GetUnprocessedBatchAsync(int batchSize, int maxAttempts, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await context.Set<OutboxMessage>()
            .Where(m => m.ProcessedOnUtc == null && m.AttemptCount < maxAttempts
                && (m.NextAttemptOnUtc == null || m.NextAttemptOnUtc <= now))
            .OrderBy(m => m.OccurredOnUtc)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public Task MarkProcessedAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        message.ProcessedOnUtc = DateTimeOffset.UtcNow;
        return SaveAsync(message, cancellationToken);
    }

    public Task MarkFailedAsync(OutboxMessage message, string error, CancellationToken cancellationToken = default)
    {
        message.AttemptCount++;
        message.LastError = error;
        return SaveAsync(message, cancellationToken);
    }

    private async Task SaveAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        context.Set<OutboxMessage>().Update(message);
        await context.SaveChangesAsync(cancellationToken);
    }
}
