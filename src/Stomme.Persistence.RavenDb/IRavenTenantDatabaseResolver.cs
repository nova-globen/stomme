using Stomme.Tenancy.Domain;

namespace Stomme.Persistence.RavenDb;

/// <summary>
/// Resolves the RavenDB database a tenant's documents live in, for database-per-tenant isolation. A single
/// <see cref="Raven.Client.Documents.IDocumentStore"/> can serve many databases, so the repository opens
/// each session against the resolved database. Returning null (the default) uses the store's default
/// database — shared-database isolation, where the tenant query filter provides separation.
/// </summary>
public interface IRavenTenantDatabaseResolver
{
    /// <summary>The database name for <paramref name="tenantId"/>, or null for the store's default database.</summary>
    string? Resolve(TenantId? tenantId);

    /// <summary>
    /// True when tenants may resolve to non-default databases. The outbox processor uses this to decide
    /// whether it must drain per-tenant databases in addition to the default one; false keeps it on the
    /// default database (the common, zero-overhead case).
    /// </summary>
    bool RoutesPerTenant => false;

    /// <summary>
    /// The per-tenant databases this resolver routes to, when it can name them (BL-176) - what the outbox drains besides the
    /// default database. Empty for a resolver that cannot enumerate its databases.
    /// </summary>
    IReadOnlyCollection<string> RoutedDatabases => Array.Empty<string>();
}

/// <summary>Default resolver: always the store's default database (shared-database isolation).</summary>
public sealed class NullRavenTenantDatabaseResolver : IRavenTenantDatabaseResolver
{
    public string? Resolve(TenantId? tenantId) => null;
}

/// <summary>
/// Database-per-tenant by convention: each non-system tenant maps to <c>{prefix}{tenantId}</c> (system scope
/// uses the default database). The host provisions these databases (see the tenant provisioner). The prefix
/// defaults to <c>tenant-</c>.
/// </summary>
public sealed class ConventionRavenTenantDatabaseResolver(string prefix = "tenant-") : IRavenTenantDatabaseResolver
{
    public string? Resolve(TenantId? tenantId) =>
        tenantId is { } id ? prefix + id.Value : null;

    public bool RoutesPerTenant => true;
}
