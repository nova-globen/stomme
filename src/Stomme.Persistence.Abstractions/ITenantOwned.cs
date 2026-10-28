using Stomme.Tenancy.Domain;

namespace Stomme.Persistence.Abstractions;

/// <summary>
/// Marks an aggregate as tenant-scoped. Stores apply tenant isolation to these per the deployment's
/// <c>IsolationMode</c> (shared-schema query filter, separate database, etc.); aggregates without
/// this marker (e.g. <c>Tenant</c>, <c>License</c>) are system-scoped.
/// </summary>
public interface ITenantOwned
{
    TenantId TenantId { get; }
}
