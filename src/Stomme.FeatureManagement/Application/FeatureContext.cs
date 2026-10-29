namespace Stomme.FeatureManagement.Application;

/// <summary>
/// The principal signals a flag evaluation targets against: the user's id (for per-user overrides and the
/// deterministic percentage rollout) and role names (for the role allow-list). This is passed in at
/// evaluation time so the feature system stands alone — it does not depend on any particular identity
/// implementation. The ambient value is produced by <see cref="IFeatureContextProvider"/>.
/// </summary>
/// <remarks>
/// Integration point (backlog item 02 — identity): once the framework's <c>ICurrentUser</c> lands, register
/// an <see cref="IFeatureContextProvider"/> that maps it onto a <see cref="FeatureContext"/>. Nothing else in
/// the module changes.
/// </remarks>
public sealed record FeatureContext(Guid? UserId = null, IReadOnlySet<string>? Roles = null)
{
    /// <summary>An empty context — anonymous, no roles. Excluded from role-gated flags and partial rollouts.</summary>
    public static readonly FeatureContext Anonymous = new();

    public IReadOnlySet<string> EffectiveRoles => Roles ?? EmptyRoles;

    private static readonly IReadOnlySet<string> EmptyRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Produces the ambient <see cref="FeatureContext"/> for the current operation (e.g. from the request's
/// authenticated principal). The default Stomme implementation reads the HTTP user; replace it to bridge
/// a different identity source.
/// </summary>
public interface IFeatureContextProvider
{
    FeatureContext GetContext();
}

/// <summary>
/// The open-host port for "is this capability licensed for the current tenant?". Optional: when no provider
/// is registered, entitlement gating is permissive (every required entitlement is treated as granted), so the
/// flag system works without a licensing module. Implement and register one to gate flags on licence
/// entitlements.
/// <para>
/// The capability asked about is a flag's required entitlement as declared by its code-registered
/// <see cref="FeatureDescriptor"/>, which is authoritative whenever one is registered for the key -- on both the
/// row and the row-less config path; the stored row value is used only when no descriptor exists.
/// </para>
/// </summary>
public interface IEntitlementProvider
{
    Task<bool> IsEntitledAsync(string capability, CancellationToken ct = default);
}
