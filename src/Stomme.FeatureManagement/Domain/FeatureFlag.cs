using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.FeatureManagement.Domain;

/// <summary>Raised when a flag's state changes (enabled or targeting); recorded to the flag-change audit trail.</summary>
public sealed record FeatureToggledEvent(Guid FlagId, string Key, bool Enabled) : DomainEvent;

/// <summary>
/// A runtime switch for a capability, scoped to a tenant. Its <em>effective</em> state (resolved by the
/// evaluator) combines: the admin toggle (<see cref="Enabled"/>); a licence entitlement
/// (<see cref="RequiredEntitlement"/>); a dependency on another flag (<see cref="RequiredFlag"/>);
/// audience targeting by role (<see cref="TargetRoles"/>) and percentage rollout
/// (<see cref="RolloutPercentage"/>); and per-user overrides (<see cref="UserOverrides"/>) that take
/// precedence over targeting.
/// </summary>
public sealed class FeatureFlag : AggregateRoot<Guid>, ITenantOwned
{
    private readonly HashSet<string> _targetRoles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, bool> _userOverrides = new();

    private FeatureFlag()
    {
        Key = string.Empty;
        TenantId = null!;
    }

    private FeatureFlag(Guid id, TenantId tenantId, string key, bool enabled, string? requiredEntitlement, string? requiredFlag)
    {
        Id = id;
        TenantId = tenantId;
        Key = key;
        Enabled = enabled;
        RequiredEntitlement = requiredEntitlement;
        RequiredFlag = requiredFlag;
        RolloutPercentage = 100;
    }

    public TenantId TenantId { get; private set; }

    public string Key { get; private set; }

    public bool Enabled { get; private set; }

    /// <summary>A capability the licence must entitle for this flag to be effectively on; null if none.</summary>
    public string? RequiredEntitlement { get; private set; }

    /// <summary>Another flag that must be effectively on for this one to be on; null if none.</summary>
    public string? RequiredFlag { get; private set; }

    /// <summary>If non-empty, only users holding one of these roles get the flag.</summary>
    public IReadOnlyCollection<string> TargetRoles => _targetRoles;

    /// <summary>Percentage of users the flag rolls out to (0–100; 100 = everyone). Deterministic per user.</summary>
    public int RolloutPercentage { get; private set; }

    /// <summary>
    /// Per-user overrides keyed by user id, evaluated ahead of role/percentage targeting: <c>false</c> is an
    /// absolute kill switch for that user; <c>true</c> force-includes the user past targeting (the flag must
    /// still be enabled, entitled, and have its required flag on).
    /// </summary>
    public IReadOnlyDictionary<Guid, bool> UserOverrides => _userOverrides;

    public static FeatureFlag Create(Guid id, TenantId tenantId, string key, bool enabled, string? requiredEntitlement = null, string? requiredFlag = null)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var flag = new FeatureFlag(
            id, tenantId, key.Trim(), enabled,
            string.IsNullOrWhiteSpace(requiredEntitlement) ? null : requiredEntitlement.Trim(),
            string.IsNullOrWhiteSpace(requiredFlag) ? null : requiredFlag.Trim());
        flag.RaiseDomainEvent(new FeatureToggledEvent(id, flag.Key, enabled));
        return flag;
    }

    /// <summary>Sets the admin toggle (idempotent); announces changes with <see cref="FeatureToggledEvent"/>.</summary>
    public void Set(bool enabled)
    {
        if (Enabled == enabled)
        {
            return;
        }

        Enabled = enabled;
        RaiseDomainEvent(new FeatureToggledEvent(Id, Key, enabled));
    }

    /// <summary>Replaces the audience targeting (role allow-list and percentage rollout).</summary>
    public void SetTargeting(IEnumerable<string> targetRoles, int rolloutPercentage)
    {
        if (rolloutPercentage is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(rolloutPercentage), "Rollout percentage must be 0–100.");
        }

        _targetRoles.Clear();
        foreach (var role in targetRoles)
        {
            if (!string.IsNullOrWhiteSpace(role))
            {
                _targetRoles.Add(role.Trim());
            }
        }

        RolloutPercentage = rolloutPercentage;
        RaiseDomainEvent(new FeatureToggledEvent(Id, Key, Enabled));
    }

    /// <summary>
    /// Sets a per-user override (idempotent): <paramref name="enabled"/> <c>false</c> denies the flag to that
    /// user outright; <c>true</c> force-includes the user past role/percentage targeting.
    /// </summary>
    public void SetUserOverride(Guid userId, bool enabled)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A user id is required.", nameof(userId));
        }

        if (_userOverrides.TryGetValue(userId, out var current) && current == enabled)
        {
            return;
        }

        _userOverrides[userId] = enabled;
        RaiseDomainEvent(new FeatureToggledEvent(Id, Key, Enabled));
    }

    /// <summary>Removes a per-user override, returning the user to the default audience rules.</summary>
    public void ClearUserOverride(Guid userId)
    {
        if (_userOverrides.Remove(userId))
        {
            RaiseDomainEvent(new FeatureToggledEvent(Id, Key, Enabled));
        }
    }
}
