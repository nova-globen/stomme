using Raven.Client.Documents;
using Raven.Client.Exceptions;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

namespace Stomme.Persistence.RavenDb;

/// <summary>
/// Creates the RavenDB databases the store addresses. Nothing else in the provider does this: the relational
/// install runs <c>Database.Migrate()</c>, and <c>IRavenDataMigration</c> is a DOCUMENT api — it patches
/// documents that already exist and cannot bring a database into being. A RavenDB store opened against a
/// database that was never created fails on first use, so the host calls this before anything reads or writes.
/// <para>
/// <b>On indexes — deliberately none.</b> An earlier scouting pass (RAVENDB-FEASIBILITY.md §6) expected this
/// to deploy static indexes for the framework store's queries. Measured against RavenDB 7.2.4, that premise is
/// false: a <i>dynamic</i> query (<c>session.Query&lt;T&gt;()</c> — the only thing the seam's
/// <c>IQuerySession</c> exposes, since it hands back a bare <see cref="IQueryable{T}"/> with no index-name
/// dimension) does NOT use a static index even when one covers the query exactly. It creates and uses its own
/// auto-index instead. Deploying static indexes here would ship dead weight; routing dynamic queries at them
/// would need either a change to the seam's contract (binding the read to RavenDB, which the seam forbids) or a
/// provider-local type-to-index map that silently returns nothing the day a query filters an unindexed field —
/// the exact silent-failure class this work exists to remove. RavenDB's auto-indexes serve every shape the
/// framework store issues; correctness across the index-build window is the staleness wait's job, not an
/// index's. See src/Stomme.Persistence.RavenDb/README.md.
/// </para>
/// </summary>
public static class RavenDatabaseBootstrap
{
    /// <summary>
    /// Creates <paramref name="database"/> if it does not exist. Idempotent, and safe to race: a concurrent
    /// creator is treated as success, not as an error.
    /// </summary>
    /// <param name="database">The database name; null/empty means the store's own default database.</param>
    /// <returns>True when this call created the database, false when it already existed.</returns>
    public static async Task<bool> EnsureDatabaseAsync(
        IDocumentStore store,
        string? database = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var name = string.IsNullOrEmpty(database) ? store.Database : database;
        if (string.IsNullOrEmpty(name))
        {
            throw new InvalidOperationException(
                "No RavenDB database to create: neither a database name nor a store default was supplied.");
        }

        var record = await store.Maintenance.Server
            .SendAsync(new GetDatabaseRecordOperation(name), ct)
            .ConfigureAwait(false);

        if (record is not null)
        {
            return false;
        }

        try
        {
            // A create whose commit notification is lost is rechecked, not waited on longer.
            await RavenClusterCommit.RunAsync(
                () => store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(name)), ct),
                async () => await store.Maintenance.Server
                    .SendAsync(new GetDatabaseRecordOperation(name), ct)
                    .ConfigureAwait(false) is not null).ConfigureAwait(false);
            return true;
        }
        catch (ConcurrencyException)
        {
            // Another node/process created it between the check and the create. RavenDB reports the
            // already-exists case as a concurrency failure; for an idempotent ensure it is success.
            return false;
        }
    }
}
