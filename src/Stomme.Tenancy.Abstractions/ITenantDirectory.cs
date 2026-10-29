using Stomme.Tenancy.Domain;

namespace Stomme.Tenancy.Abstractions;

/// <summary>
/// Enumerates every tenant the installation knows about — for the operations that must run once *per
/// tenant* rather than for the ambient one: seeding, migrations, per-tenant background sweeps.
/// <para>
/// This is the companion <see cref="ITenantContext"/> could not be: <see cref="ITenantContext"/> answers
/// "which tenant am I in?", which is unanswerable at startup, where nothing has resolved an alias yet and
/// the work is inherently cross-tenant. Pair it with <see cref="Persistence.Abstractions.ITenantScopeFactory"/>
/// to enter each tenant's scope in turn.
/// </para>
/// <para>
/// Implemented by the host over its own tenant store, and treated as OPTIONAL by consumers: a minimal host
/// or an isolated test may register neither this nor a tenant scope factory, in which case the caller falls
/// back to the single logical tenant (<see cref="TenantId.Default"/>).
/// </para>
/// </summary>
public interface ITenantDirectory
{
    /// <summary>
    /// Every known tenant, in a stable order. Never null; empty before the installation has a tenant store
    /// (the caller decides what that means — typically "fall back to the default tenant").
    /// <para>
    /// Async because enumerating tenants IS a store read — the host's implementation queries its tenant store.
    /// Every caller is startup/seeding-time cross-tenant work that already runs on an async path, so
    /// there is nothing here that must resolve a tenant list from inside a synchronous platform virtual.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<TenantId>> GetTenantIdsAsync();
}
