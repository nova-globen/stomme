using Stomme.Persistence.Abstractions;
using Raven.Client.Documents;
using Raven.Client.Documents.Linq;

namespace Stomme.Persistence.RavenDb.Migrations;

/// <summary>
/// Runs pending <see cref="IRavenDataMigration"/> migrations in order, once per cluster (guarded by an
/// <see cref="IDistributedLock"/>), recording each in history. A migration and its success record are
/// written in the same session so they commit atomically; a failure is recorded separately and stops
/// the run (fail-fast for upgrades). Already-succeeded migrations are skipped.
/// </summary>
public sealed class RavenDataMigrationRunner(
    IDocumentStore store,
    IEnumerable<IRavenDataMigration> migrations,
    IDistributedLock distributedLock)
{
    private const string RecordPrefix = "DataMigrations/";

    public async Task<IReadOnlyList<RavenMigrationRecord>> RunPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var handle = await distributedLock
            .AcquireAsync("raven:data-migrations", TimeSpan.FromMinutes(10), cancellationToken)
            .ConfigureAwait(false);
        if (handle is null)
        {
            return []; // another node is applying migrations
        }

        var applied = await LoadSucceededIdsAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<RavenMigrationRecord>();

        foreach (var migration in migrations.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            if (applied.Contains(migration.Id))
            {
                continue;
            }

            RavenMigrationRecord record;
            try
            {
                using var session = store.OpenAsyncSession();
                await migration.ApplyAsync(session, cancellationToken).ConfigureAwait(false);
                record = new RavenMigrationRecord { Id = migration.Id, AppliedUtc = DateTimeOffset.UtcNow, Success = true };
                await session.StoreAsync(record, RecordPrefix + migration.Id, cancellationToken).ConfigureAwait(false);
                await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                record = new RavenMigrationRecord
                {
                    Id = migration.Id,
                    AppliedUtc = DateTimeOffset.UtcNow,
                    Success = false,
                    Error = ex.Message,
                };
                await RecordAsync(record, cancellationToken).ConfigureAwait(false);
                results.Add(record);
                break; // fail-fast: do not apply later migrations on top of a failed one
            }

            results.Add(record);
        }

        return results;
    }

    private async Task<HashSet<string>> LoadSucceededIdsAsync(CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var records = await session.Query<RavenMigrationRecord>()
            .Customize(customize => customize.WaitForNonStaleResults())
            .Where(record => record.Success)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return records.Select(record => record.Id).ToHashSet(StringComparer.Ordinal);
    }

    private async Task RecordAsync(RavenMigrationRecord record, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        await session.StoreAsync(record, RecordPrefix + record.Id, cancellationToken).ConfigureAwait(false);
        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
