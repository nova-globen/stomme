using Stomme.FeatureManagement.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Stomme.Tenancy.Domain;

namespace Stomme.FeatureManagement.Application;

/// <summary>A feature flag projected for display: configuration, catalog metadata, and effective state.</summary>
public sealed record FeatureView(
    string Key,
    bool Enabled,
    string? RequiredEntitlement,
    string? RequiredFlag,
    IReadOnlyList<string> TargetRoles,
    int RolloutPercentage,
    bool Effective,
    string? Description = null,
    string? Category = null,
    IReadOnlyDictionary<Guid, bool>? UserOverrides = null);

/// <summary>An entry in a flag's change history, projected for display.</summary>
public sealed record FeatureAuditView(string Key, bool Enabled, string? ChangedBy, DateTimeOffset ChangedAtUtc);

/// <summary>
/// The write/read application surface for feature flags: toggle, target, override, and list — plus the
/// change history. Mutations flow through the aggregate (raising <see cref="FeatureToggledEvent"/>) and the
/// unit of work (driving the audit trail). This stands in for the CQRS command/query handlers of backlog
/// item 07; when the mediator lands, these methods become thin command/query wrappers.
/// </summary>
public interface IFeatureAdminService
{
    Task<Guid> ToggleAsync(string key, bool enabled, CancellationToken ct = default);
    Task<Guid> SetTargetingAsync(string key, IReadOnlyList<string> targetRoles, int rolloutPercentage, CancellationToken ct = default);
    Task<Guid> SetUserOverrideAsync(string key, Guid userId, bool? enabled, CancellationToken ct = default);
    Task<IReadOnlyList<FeatureView>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FeatureAuditView>> ListAuditsAsync(string? key = null, int take = 100, CancellationToken ct = default);
}

internal sealed class FeatureAdminService(
    IRepository<FeatureFlag, Guid> flags,
    IRepository<FeatureFlagAudit, Guid> audits,
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext,
    IFeatureEvaluator evaluator,
    IFeatureRegistry registry,
    IFeatureFlagCache cache) : IFeatureAdminService
{
    // Every mutation below invalidates the evaluator cache for the flag's OWN tenant — the exact
    // owner even for a system-scope write that reaches another tenant's flag — and does so AFTER the write
    // committed: both store providers commit inside the repository call, and the unit-of-work call after it
    // only dispatches domain events (staging the audit row), so the invalidation is strictly post-commit.
    // Invalidating earlier would let a concurrent evaluation re-cache pre-write data after the drop.

    /// <summary>Enables or disables a feature flag for the current tenant (creating it if new); returns the flag id.</summary>
    public async Task<Guid> ToggleAsync(string key, bool enabled, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var existing = await FindAsync(key, ct);
        if (existing is null)
        {
            var flag = FeatureFlag.Create(Guid.NewGuid(), tenantContext.CurrentTenantId ?? TenantId.Default, key, enabled);
            await flags.AddAsync(flag, ct);
            await unitOfWork.SaveChangesAsync(ct);
            cache.Invalidate(flag.TenantId);
            return flag.Id;
        }

        existing.Set(enabled);
        await flags.UpdateAsync(existing, ct);
        await unitOfWork.SaveChangesAsync(ct);
        cache.Invalidate(existing.TenantId);
        return existing.Id;
    }

    /// <summary>Sets a flag's audience targeting (role allow-list and percentage rollout); returns the flag id.</summary>
    public async Task<Guid> SetTargetingAsync(string key, IReadOnlyList<string> targetRoles, int rolloutPercentage, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (rolloutPercentage is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(rolloutPercentage), "Rollout percentage must be 0–100.");
        }

        var flag = await FindAsync(key, ct)
            ?? throw new InvalidOperationException($"Feature flag '{key}' was not found.");

        flag.SetTargeting(targetRoles ?? [], rolloutPercentage);
        await flags.UpdateAsync(flag, ct);
        await unitOfWork.SaveChangesAsync(ct);
        cache.Invalidate(flag.TenantId);
        return flag.Id;
    }

    /// <summary>
    /// Sets or clears a per-user override on a flag: a non-null <paramref name="enabled"/> pins the flag
    /// on/off for the user ahead of targeting, while null removes any existing override. Returns the flag id.
    /// </summary>
    public async Task<Guid> SetUserOverrideAsync(string key, Guid userId, bool? enabled, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A user id is required.", nameof(userId));
        }

        var flag = await FindAsync(key, ct)
            ?? throw new InvalidOperationException($"Feature flag '{key}' was not found.");

        if (enabled is { } value)
        {
            flag.SetUserOverride(userId, value);
        }
        else
        {
            flag.ClearUserOverride(userId);
        }

        await flags.UpdateAsync(flag, ct);
        await unitOfWork.SaveChangesAsync(ct);
        cache.Invalidate(flag.TenantId);
        return flag.Id;
    }

    /// <summary>Lists the current tenant's feature flags with their effective state.</summary>
    public async Task<IReadOnlyList<FeatureView>> ListAsync(CancellationToken ct = default)
    {
        var all = (await flags.GetAllAsync(ct)).OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToList();
        var views = new List<FeatureView>(all.Count);
        foreach (var flag in all)
        {
            var descriptor = registry.Find(flag.Key);
            views.Add(new FeatureView(
                flag.Key, flag.Enabled, flag.RequiredEntitlement, flag.RequiredFlag,
                flag.TargetRoles.OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList(), flag.RolloutPercentage,
                await evaluator.IsEnabledAsync(flag.Key, ct),
                descriptor?.Description, descriptor?.Category,
                flag.UserOverrides.ToDictionary(o => o.Key, o => o.Value)));
        }

        return views;
    }

    /// <summary>Lists the current tenant's flag-change history, most recent first, optionally narrowed to one key.</summary>
    public async Task<IReadOnlyList<FeatureAuditView>> ListAuditsAsync(string? key = null, int take = 100, CancellationToken ct = default)
    {
        IEnumerable<FeatureFlagAudit> all = await audits.GetAllAsync(ct);
        if (!string.IsNullOrWhiteSpace(key))
        {
            var trimmed = key.Trim();
            all = all.Where(a => string.Equals(a.Key, trimmed, StringComparison.OrdinalIgnoreCase));
        }

        return all
            .OrderByDescending(a => a.ChangedAtUtc)
            .Take(Math.Max(1, take))
            .Select(a => new FeatureAuditView(a.Key, a.Enabled, a.ChangedBy, a.ChangedAtUtc))
            .ToList();
    }

    private async Task<FeatureFlag?> FindAsync(string key, CancellationToken ct)
    {
        var trimmed = key.Trim();
        return (await flags.GetAllAsync(ct))
            .FirstOrDefault(f => string.Equals(f.Key, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
