using Stomme.Tenancy.Domain;

namespace Stomme.Tenancy.Abstractions;

/// <summary>
/// The store binding for a tenant under non-shared isolation: the connection string to use and, for
/// schema-per-tenant, the schema. <see cref="Schema"/> is null for shared-schema and database-per-tenant.
/// </summary>
public sealed record TenantConnection(string ConnectionString, string? Schema = null);

/// <summary>
/// Resolves the per-tenant store binding for the stronger isolation modes (database-per-tenant,
/// schema-per-tenant). The store layer consults this when building a tenant's <c>DbContext</c>/session so
/// the connection (and schema) follow the tenant. Shared-schema deployments never call it (the shared
/// connection + tenant query filter cover isolation).
/// </summary>
public interface ITenantConnectionResolver
{
    /// <summary>
    /// Async because a tenant's store binding LIVES IN THE TENANT STORE — the host reads it (D32). Callers
    /// must therefore resolve the binding where awaiting is legal (building a context in
    /// <c>IDbContextFactory.CreateDbContextAsync</c>), NOT from inside a synchronous platform virtual such as
    /// <c>DbContext.OnConfiguring</c> or a synchronous DI factory lambda.
    /// </summary>
    Task<TenantConnection> ResolveAsync(TenantId tenantId);
}
