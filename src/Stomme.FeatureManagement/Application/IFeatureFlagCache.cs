using Stomme.FeatureManagement.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.FeatureManagement.Application;

/// <summary>
/// An immutable projection of exactly the <see cref="FeatureFlag"/> fields evaluation reads. The cache holds
/// snapshots, never aggregates: a cached aggregate would be a live, mutable domain object (with EF/Raven
/// session baggage) shared across scopes and threads, where one caller's mutation silently becomes every
/// caller's answer. A snapshot cannot change an answer — it carries the same values the evaluator would have
/// read from the store, and nothing else.
/// </summary>
internal sealed record FeatureFlagSnapshot(
    string Key,
    bool Enabled,
    string? RequiredEntitlement,
    string? RequiredFlag,
    IReadOnlySet<string> TargetRoles,
    int RolloutPercentage,
    IReadOnlyDictionary<Guid, bool> UserOverrides)
{
    public static FeatureFlagSnapshot From(FeatureFlag flag) => new(
        flag.Key,
        flag.Enabled,
        flag.RequiredEntitlement,
        flag.RequiredFlag,
        new HashSet<string>(flag.TargetRoles, StringComparer.OrdinalIgnoreCase),
        flag.RolloutPercentage,
        new Dictionary<Guid, bool>(flag.UserOverrides));
}

/// <summary>
/// The singleton per-tenant cache of feature-flag snapshots the evaluator reads through (BL-39), so N
/// evaluations of a flag cost one store round trip instead of N — including evaluations inside background
/// jobs, which run in their own scopes over this same singleton.
/// <para>
/// <b>What is cached — and what deliberately is NOT.</b> Only the DB flag rows (per tenant) are cached.
/// The D84 precedence (config → DB → catalog default) is untouched: the config source is consulted LIVE on
/// every evaluation (an in-memory <c>IConfiguration</c> read — already cheap, and hot-reloadable), and the
/// entitlement provider is consulted live too. The cache therefore cannot change any answer; it only makes
/// the DB leg of an unchanged evaluation cheaper.
/// </para>
/// <para>
/// <b>The key is the tenant.</b> Entries are keyed by the SAME tenant identity the store's tenant query
/// filter reads by (<c>ITenantContext.CurrentTenantId</c>), so a cache hit can never serve one tenant's
/// flags to another — the BL-41 class of defect, inverted. System-scope and null-tenant reads are never
/// cached (the evaluator bypasses the cache for them), so no cross-tenant flag list ever enters the cache.
/// </para>
/// <para>
/// <b>Invalidation is exact in-process.</b> Every path that mutates a <see cref="FeatureFlag"/> (the admin
/// service's three mutations and the startup seeder) invalidates the owning tenant's entry AFTER its write
/// committed — both store providers commit inside the repository call, so post-save invalidation is
/// strictly post-commit. The entry TTL exists only to bound how long ANOTHER process's write can go unseen
/// (multi-node deployments have no cross-node invalidation signal here); expiry merely forces a fresh store
/// read, which by construction cannot change an answer.
/// </para>
/// </summary>
internal interface IFeatureFlagCache
{
    /// <summary>
    /// Returns the tenant's flag snapshots from cache, or loads them via <paramref name="load"/> and caches
    /// the result. A load raced by an invalidation returns its (then-current) data to its own caller but is
    /// never cached — data read before a write must not be served after it.
    /// </summary>
    Task<IReadOnlyList<FeatureFlagSnapshot>> GetOrLoadAsync(
        TenantId tenantId,
        Func<CancellationToken, Task<IReadOnlyList<FeatureFlagSnapshot>>> load,
        CancellationToken ct = default);

    /// <summary>Drops the tenant's entry; call after a flag write for that tenant has committed.</summary>
    void Invalidate(TenantId tenantId);
}
