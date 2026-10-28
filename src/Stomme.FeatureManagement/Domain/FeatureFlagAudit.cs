using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.FeatureManagement.Domain;

/// <summary>
/// An append-only record of a feature flag change — who changed which flag to what state, and when.
/// Written by the audit handler in response to <see cref="FeatureToggledEvent"/>, it is the queryable
/// change history for a flag (the event itself is transient).
/// </summary>
public sealed class FeatureFlagAudit : AggregateRoot<Guid>, ITenantOwned
{
    private FeatureFlagAudit()
    {
        TenantId = null!;
        Key = string.Empty;
    }

    private FeatureFlagAudit(Guid id, TenantId tenantId, Guid flagId, string key, bool enabled, string? changedBy, DateTimeOffset changedAtUtc)
    {
        Id = id;
        TenantId = tenantId;
        FlagId = flagId;
        Key = key;
        Enabled = enabled;
        ChangedBy = changedBy;
        ChangedAtUtc = changedAtUtc;
    }

    public TenantId TenantId { get; private set; }

    /// <summary>The flag that changed.</summary>
    public Guid FlagId { get; private set; }

    /// <summary>The flag's key, denormalised so the trail is readable without joining to the flag.</summary>
    public string Key { get; private set; }

    /// <summary>The flag's admin-toggle state at the moment of the change.</summary>
    public bool Enabled { get; private set; }

    /// <summary>Who made the change (user name or id), or null when no principal was in scope (e.g. seeding).</summary>
    public string? ChangedBy { get; private set; }

    public DateTimeOffset ChangedAtUtc { get; private set; }

    public static FeatureFlagAudit Record(TenantId tenantId, Guid flagId, string key, bool enabled, string? changedBy, DateTimeOffset changedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return new FeatureFlagAudit(Guid.NewGuid(), tenantId, flagId, key, enabled, changedBy, changedAtUtc);
    }
}
