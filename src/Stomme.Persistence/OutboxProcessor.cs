using Stomme.Persistence.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stomme.Persistence;

/// <summary>
/// Drains the transactional outbox: reads a batch of pending <see cref="OutboxMessage"/>s, dispatches
/// each to its handlers under the tenant it was raised in, and marks it processed or failed. Exposed
/// as a callable unit (rather than only a background loop) so it is deterministically testable.
/// Delivery is at-least-once: a crash between dispatch and mark redelivers, so handlers must be
/// idempotent. A distributed lock (when available) keeps concurrent instances from double-processing.
/// </summary>
public sealed class OutboxProcessor(
    IServiceScopeFactory scopeFactory, IOptions<OutboxOptions> options, ILogger<OutboxProcessor> logger)
{
    private const string LockKey = "outbox:process";

    /// <summary>Processes one batch; returns the number of messages successfully dispatched.</summary>
    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var store = services.GetRequiredService<IOutboxStore>();
        var dispatcher = services.GetRequiredService<IDomainEventDispatcher>();
        var serializer = services.GetRequiredService<IDomainEventSerializer>();
        var tenantScopes = services.GetService<ITenantScopeFactory>();
        var distributedLock = services.GetService<IDistributedLock>();

        await using var handle = distributedLock is null
            ? null
            : await distributedLock.AcquireAsync(LockKey, options.Value.LockDuration, cancellationToken);
        if (distributedLock is not null && handle is null)
        {
            return 0; // another node is draining the outbox
        }

        var maxAttempts = options.Value.MaxAttempts;
        var batch = await store.GetUnprocessedBatchAsync(options.Value.BatchSize, maxAttempts, cancellationToken);
        var processed = 0;
        foreach (var message in batch)
        {
            var domainEvent = serializer.Deserialize(message.EventType, message.Payload);
            if (domainEvent is null)
            {
                await FailAsync(store, message, $"Unknown event type '{message.EventType}'.", maxAttempts, cancellationToken);
                continue;
            }

            try
            {
                using var tenantScope = tenantScopes?.BeginScope(message.TenantId);
                await dispatcher.DispatchAsync([domainEvent], cancellationToken);
                await store.MarkProcessedAsync(message, cancellationToken);
                processed++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox message {MessageId} ({EventType}) failed to dispatch.", message.Id, message.EventType);
                await FailAsync(store, message, ex.Message, maxAttempts, cancellationToken);
            }
        }

        return processed;
    }

    private async Task FailAsync(IOutboxStore store, OutboxMessage message, string error, int maxAttempts, CancellationToken cancellationToken)
    {
        // Schedule the next attempt from the exponential backoff curve before recording the failure;
        // AttemptCount is still the count of prior failures here (the store increments it on mark).
        message.NextAttemptOnUtc = DateTimeOffset.UtcNow + options.Value.BackoffFor(message.AttemptCount);
        await store.MarkFailedAsync(message, error, cancellationToken);
        if (message.AttemptCount >= maxAttempts)
        {
            logger.LogWarning(
                "Outbox message {MessageId} ({EventType}) dead-lettered after {Attempts} attempts: {Error}",
                message.Id, message.EventType, message.AttemptCount, error);
        }
    }
}
