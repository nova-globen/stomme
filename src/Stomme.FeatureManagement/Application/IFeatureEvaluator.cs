namespace Stomme.FeatureManagement.Application;

/// <summary>
/// The open-host port for "is this feature on for the current tenant and user?". Implemented by the Feature
/// Management module; consumed by any code that gates a capability on a flag. A flag resolves to on only
/// when it is enabled, its required entitlement is licensed, its required flag is on, and the evaluation
/// context matches the audience targeting (role allow-list and deterministic percentage rollout), with any
/// per-user override taking precedence over targeting.
/// </summary>
public interface IFeatureEvaluator
{
    /// <summary>Evaluates <paramref name="key"/> for the ambient <see cref="FeatureContext"/>.</summary>
    Task<bool> IsEnabledAsync(string key, CancellationToken ct = default);

    /// <summary>Evaluates <paramref name="key"/> for an explicit <paramref name="context"/> (user id + roles).</summary>
    Task<bool> IsEnabledAsync(string key, FeatureContext context, CancellationToken ct = default);
}
