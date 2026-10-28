using Stomme.Tenancy.Domain;

namespace Stomme.Tenancy.Abstractions;

/// <summary>
/// The ambient tenant scope that application code reads to stay tenant-agnostic. Tenant-owned
/// operations consult <see cref="CurrentTenantId"/>; system-wide operations (health, licensing,
/// global flags, setup) run under <see cref="IsSystemScope"/>.
/// </summary>
public interface ITenantContext
{
    /// <summary>The current tenant, or null when running in system scope.</summary>
    TenantId? CurrentTenantId { get; }

    /// <summary>True when no tenant is in scope — i.e. a cross-tenant / super-admin operation.</summary>
    bool IsSystemScope { get; }
}
