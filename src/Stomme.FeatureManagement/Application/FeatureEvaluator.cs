using System.Security.Cryptography;
using System.Text;
using Stomme.FeatureManagement.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;

namespace Stomme.FeatureManagement.Application;

/// <summary>
/// Resolves a feature flag to its effective on/off state for the current tenant and evaluation context — the
/// implementation behind the <see cref="IFeatureEvaluator"/> port that service modules consult before
/// activating a capability. A flag is on only when it is enabled; its required entitlement is licensed (via
/// <see cref="IEntitlementProvider"/>, permissive when no provider is present); its required flag is itself
/// on; and the context matches the audience targeting (role allow-list and deterministic percentage
/// rollout). A per-user override takes precedence over targeting — OFF denies the user outright, ON
/// force-includes them — without bypassing the enabled, entitlement, or required-flag gates.
/// </summary>
/// <remarks>
/// <b>Configuration precedence: config -&gt; DB -&gt; catalog default.</b> An
/// <see cref="IFeatureConfigurationSource"/> (a <c>features.json</c> / <c>appsettings</c> section / env var /
/// Azure App Configuration) is consulted first: when it declares a definite state for the key, that value
/// overrides the admin toggle and the catalog default. Config sets the <em>enabled switch</em> only -- it does
/// NOT bypass entitlement licensing, the required-flag dependency, or a per-user OFF kill switch (an override
/// never grants a licence -- the module's stated invariant), so those gates still run. When config has no
/// opinion (<c>null</c>), evaluation falls through to exactly the historical DB -&gt; default behaviour, so a
/// stock install that declares no feature configuration is byte-for-byte unchanged. A config value with no
/// backing DB row turns the flag on with nothing else to gate it -- the M5 <c>module.&lt;id&gt;.enabled</c> knob.
/// <para>
/// <b>The DB leg reads through the singleton <see cref="IFeatureFlagCache"/></b>, keyed by the SAME
/// tenant identity the store's tenant query filter reads by, so N evaluations cost one store round trip.
/// Config and entitlements are still consulted LIVE on every evaluation — the cache fronts only the flag
/// rows, so no answer changes (see the cache's own remarks for the full argument). System-scope and
/// null-tenant reads bypass the cache entirely: neither is a per-tenant answer, so caching either would be
/// the cross-tenant-leak shape.
/// </para>
/// <para>
/// <b>The code-declared descriptor is authoritative for the required entitlement.</b> When the
/// <see cref="IFeatureRegistry"/> holds a <see cref="FeatureDescriptor"/> for the key, ITS
/// <see cref="FeatureDescriptor.RequiredEntitlement"/> is the only entitlement consulted -- on the row path AND on
/// the row-less config path. The row's stored value applies only when no descriptor is registered. This closes
/// two holes: a config-true key with no backing row used to turn on with no entitlement gate at all (D84's
/// row-less bypass), and a release whose descriptor starts (or stops) requiring an entitlement never reached the
/// existing row (<see cref="FeatureFlag.RequiredEntitlement"/> has no mutator and the seeder only creates missing
/// rows). A stale row value for an uninstalled module (no descriptor) still applies and stays denied -- harmless,
/// nothing consumes that key. With no <see cref="IEntitlementProvider"/> registered the gate stays permissive.
/// </para>
/// </remarks>
internal sealed class FeatureEvaluator(
    IRepository<FeatureFlag, Guid> flags,
    IFeatureContextProvider contextProvider,
    IFeatureConfigurationSource configuration,
    IEnumerable<IEntitlementProvider> entitlements,
    IFeatureFlagCache cache,
    ITenantContext tenantContext,
    IFeatureRegistry registry) : IFeatureEvaluator
{
    private readonly IEntitlementProvider? _entitlements = entitlements.FirstOrDefault();

    public Task<bool> IsEnabledAsync(string key, CancellationToken ct = default) =>
        IsEnabledAsync(key, contextProvider.GetContext(), ct);

    public Task<bool> IsEnabledAsync(string key, FeatureContext context, CancellationToken ct = default) =>
        IsEnabledAsync(key, context, new HashSet<string>(StringComparer.OrdinalIgnoreCase), ct);

    private async Task<bool> IsEnabledAsync(string key, FeatureContext context, HashSet<string> visited, CancellationToken ct)
    {
        if (!visited.Add(key))
        {
            return false; // dependency cycle — treat as off
        }

        // Precedence step 1: the config source is asked first. A non-null answer overrides the admin
        // toggle and the catalog default; null means "no opinion — use the DB flag / default", so the paths
        // below collapse to the historical behaviour and a stock install is byte-for-byte unchanged.
        var configState = configuration.GetConfiguredState(key);

        var flag = (await GetTenantFlagsAsync(ct)).FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        if (flag is null)
        {
            // No per-tenant flag exists: config is the only possible source of truth. Present-and-true turns
            // the flag on (the M5 module.<id>.enabled knob) -- but a registered descriptor's required entitlement
            // still gates it (config sets the enabled switch, it never grants a licence). Config-false or an
            // absent config leaves it off — exactly the historical "unknown flag is off".
            return configState == true && await IsEntitledAsync(EffectiveRequiredEntitlement(key, null), ct);
        }

        // A per-user override is the highest-priority audience signal. OFF is an absolute kill switch for the
        // user; ON force-includes them past role/percentage targeting (but the flag must still be enabled,
        // entitled, and have its required flag on — overrides never bypass licensing).
        bool? userOverride = context.UserId is { } userId && flag.UserOverrides.TryGetValue(userId, out var ov)
            ? ov
            : null;
        if (userOverride == false)
        {
            return false;
        }

        // Config, when present, replaces the admin toggle as the enabled switch; otherwise the DB toggle
        // stands. Config OFF is a hard off — it wins over a per-user force-include below, exactly as an admin
        // toggle off does.
        var enabled = configState ?? flag.Enabled;
        if (!enabled)
        {
            return false;
        }

        // The registered descriptor, when present, is authoritative over the row's stored entitlement.
        if (!await IsEntitledAsync(EffectiveRequiredEntitlement(flag.Key, flag.RequiredEntitlement), ct))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(flag.RequiredFlag) && !await IsEnabledAsync(flag.RequiredFlag, context, visited, ct))
        {
            return false;
        }

        return userOverride == true || MatchesAudience(flag, context);
    }

    /// <summary>
    /// The entitlement that gates <paramref name="key"/>: the registered descriptor's value when a descriptor
    /// exists (even when it declares none), otherwise the stored row value.
    /// </summary>
    private string? EffectiveRequiredEntitlement(string key, string? rowValue) =>
        registry.Find(key) is { } descriptor ? descriptor.RequiredEntitlement : rowValue;

    /// <summary>
    /// True when no entitlement is required, when no <see cref="IEntitlementProvider"/> is registered (permissive
    /// by design), or when the provider licenses it.
    /// </summary>
    private async Task<bool> IsEntitledAsync(string? requiredEntitlement, CancellationToken ct) =>
        string.IsNullOrEmpty(requiredEntitlement)
        || _entitlements is null
        || await _entitlements.IsEntitledAsync(requiredEntitlement, ct);

    /// <summary>
    /// The tenant's flag snapshots — through the cache when the read is a per-tenant one. The cache key is
    /// the ambient tenant the store's tenant query filter reads by; system scope reads NO rows, and a
    /// null tenant (matches nothing) goes straight to the store, exactly the pre-cache behaviour.
    /// </summary>
    private Task<IReadOnlyList<FeatureFlagSnapshot>> GetTenantFlagsAsync(CancellationToken ct)
    {
        // SYSTEM scope has no tenant, so NO tenant's row applies - only installation-wide configuration decides
        // (the row-less path above). It used to load every tenant's rows (the store's filter admits all of them in system
        // scope) and take the first with the key: a job that had not entered a tenant got some tenant's switch, whichever
        // the store happened to list first (measured, FeatureEvaluatorSystemScopeTests).
        if (tenantContext.IsSystemScope)
        {
            return Task.FromResult<IReadOnlyList<FeatureFlagSnapshot>>(Array.Empty<FeatureFlagSnapshot>());
        }

        if (tenantContext.CurrentTenantId is not { } tenantId)
        {
            return LoadSnapshotsAsync(ct);
        }

        return cache.GetOrLoadAsync(tenantId, LoadSnapshotsAsync, ct);
    }

    private async Task<IReadOnlyList<FeatureFlagSnapshot>> LoadSnapshotsAsync(CancellationToken ct) =>
        (await flags.GetAllAsync(ct)).Select(FeatureFlagSnapshot.From).ToArray();

    private static bool MatchesAudience(FeatureFlagSnapshot flag, FeatureContext context)
    {
        if (flag.TargetRoles.Count > 0)
        {
            var roles = context.EffectiveRoles;
            if (!flag.TargetRoles.Any(roles.Contains))
            {
                return false;
            }
        }

        if (flag.RolloutPercentage < 100)
        {
            // Anonymous users are excluded from a partial rollout (no stable bucket).
            return context.UserId is { } userId && Bucket(userId, flag.Key) < flag.RolloutPercentage;
        }

        return true;
    }

    // A stable 0–99 bucket for (user, flag), so a user's rollout membership is deterministic across requests.
    private static int Bucket(Guid userId, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{userId:N}:{key}"));
        return (int)(BitConverter.ToUInt32(hash, 0) % 100);
    }
}
