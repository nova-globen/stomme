namespace Stomme.Security.Abstractions;

/// <summary>
/// The open-host port for "is this feature on for the current tenant and user?". Implemented by the
/// Feature Management module; consumed by any context — and by the declarative <see cref="RequiresAttribute"/>
/// access guard — that gates a capability on a flag, without taking a dependency on the Feature
/// Management module itself. A flag resolves to on only when it is enabled, its required entitlement is
/// licensed, its required flag is on, and the current user matches the audience targeting.
/// </summary>
public interface IFeatureEvaluator
{
    Task<bool> IsEnabledAsync(string key, CancellationToken ct = default);
}
