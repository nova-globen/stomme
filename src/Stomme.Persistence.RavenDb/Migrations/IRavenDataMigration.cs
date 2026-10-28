using Raven.Client.Documents.Session;

namespace Stomme.Persistence.RavenDb.Migrations;

/// <summary>
/// A versioned RavenDB data-patch migration (RavenDB has no schema migrations; data is patched by
/// scripts run during setup/upgrade — see docs/features/03.persistence.md). Implementations must be
/// idempotent: the runner records applied migrations, but a migration may be retried if recording its
/// success was interrupted. Ordered by <see cref="Id"/> (use a sortable prefix, e.g. <c>0001_...</c>).
/// </summary>
public interface IRavenDataMigration
{
    string Id { get; }

    Task ApplyAsync(IAsyncDocumentSession session, CancellationToken cancellationToken = default);
}
