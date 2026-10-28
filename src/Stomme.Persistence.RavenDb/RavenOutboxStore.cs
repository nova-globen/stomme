using System.Collections.Concurrent;
using Stomme.Persistence.Abstractions;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;
using Raven.Client.Documents.Session;

namespace Stomme.Persistence.RavenDb;

/// <summary>
/// RavenDB <see cref="IOutboxStore"/>. Outbox documents are written into the same session as the aggregate
/// by <see cref="RavenRepository{TAggregate, TId}"/>, so under database-per-tenant routing they land in the
/// tenant's database alongside the aggregate. This store therefore drains the default database plus every
/// per-tenant database (when the resolver routes per tenant), and remembers which database each message came
/// from so its status update goes back to the same database. With the default (shared-database) resolver it
/// drains only the default database — the common, zero-overhead path.
/// </summary>
internal sealed class RavenOutboxStore(
    IDocumentStore store,
    IRavenTenantDatabaseResolver databaseResolver,
    IServiceScopeFactory scopeFactory) : IOutboxStore
{
    // Message id -> source database (null = default). Populated on read, consulted on mark-back.
    private readonly ConcurrentDictionary<Guid, string?> _messageDatabases = new();

    public async Task<IReadOnlyList<OutboxMessage>> GetUnprocessedBatchAsync(int batchSize, int maxAttempts, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var collected = new List<OutboxMessage>();
        foreach (var database in await DatabasesToDrainAsync(cancellationToken))
        {
            using var session = OpenSession(database);
            var batch = await session.Query<OutboxMessage>()
                .Customize(c => c.WaitForNonStaleResults())
                .Where(m => m.ProcessedOnUtc == null && m.AttemptCount < maxAttempts
                    && (m.NextAttemptOnUtc == null || m.NextAttemptOnUtc <= now))
                .OrderBy(m => m.OccurredOnUtc)
                .Take(batchSize)
                .ToListAsync(cancellationToken);

            foreach (var message in batch)
            {
                _messageDatabases[message.Id] = database;
            }

            collected.AddRange(batch);
        }

        // Oldest-first across all databases, capped at the batch size.
        return collected.OrderBy(m => m.OccurredOnUtc).Take(batchSize).ToList();
    }

    public Task MarkProcessedAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        message.ProcessedOnUtc = DateTimeOffset.UtcNow;
        return PersistAsync(message, cancellationToken);
    }

    public Task MarkFailedAsync(OutboxMessage message, string error, CancellationToken cancellationToken = default)
    {
        message.AttemptCount++;
        message.LastError = error;
        return PersistAsync(message, cancellationToken);
    }

    private async Task PersistAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var database = _messageDatabases.TryGetValue(message.Id, out var db) ? db : null;
        using var session = OpenSession(database);
        await session.StoreAsync(message, $"OutboxMessages/{message.Id}", cancellationToken);
        await session.SaveChangesAsync(cancellationToken);

        if (message.ProcessedOnUtc is not null)
        {
            _messageDatabases.TryRemove(message.Id, out _);
        }
    }

    private IAsyncDocumentSession OpenSession(string? database) =>
        string.IsNullOrEmpty(database)
            ? store.OpenAsyncSession()
            : store.OpenAsyncSession(new SessionOptions { Database = database });

    // The default database, and (BL-176) every tenant database the resolver routes to - an aggregate's outbox message is
    // written in the aggregate's own session, so under per-tenant routing it is in the tenant's database. A resolver that
    // cannot name its databases (the convention one) drains the default database only, as before.
    private Task<IReadOnlyList<string?>> DatabasesToDrainAsync(CancellationToken cancellationToken)
    {
        _ = scopeFactory;
        IReadOnlyList<string?> databases = databaseResolver.RoutesPerTenant
            ? new List<string?> { null }.Concat(databaseResolver.RoutedDatabases.Select(database => (string?)database)).ToList()
            : new List<string?> { null };
        return Task.FromResult(databases);
    }
}
