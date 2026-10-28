using Stomme.FeatureManagement.Application;
using Microsoft.FeatureManagement;

namespace Stomme.FeatureManagement.Infrastructure;

/// <summary>
/// Bridges the standard <see cref="IFeatureManager"/> abstraction (Microsoft.FeatureManagement) onto
/// Stomme's <see cref="IFeatureEvaluator"/>, so consumers can gate on the conventional API — including
/// <c>&lt;FeatureGate&gt;</c>-style checks — while evaluation keeps Stomme's richer semantics: per-tenant
/// flags, licence entitlements, flag dependencies, role targeting, percentage rollout, and per-user
/// overrides, all backed by the persistence seam.
/// </summary>
/// <remarks>
/// Why not Microsoft.FeatureManagement's own evaluation pipeline: its feature definitions come from
/// configuration and its filters have no notion of a tenant-scoped, admin-editable, audited flag document.
/// Taking its abstraction (and leaving the engine to Stomme) gives module authors a familiar surface
/// without giving up targeting, overrides, or the audit trail. Passing a <see cref="FeatureContext"/> as the
/// context argument evaluates against an explicit principal instead of the ambient one.
/// </remarks>
internal sealed class StommeFeatureManager(IFeatureEvaluator evaluator, IFeatureRegistry registry) : IFeatureManager
{
    public async IAsyncEnumerable<string> GetFeatureNamesAsync()
    {
        foreach (var descriptor in registry.Known)
        {
            yield return descriptor.Key;
        }

        await Task.CompletedTask;
    }

    public Task<bool> IsEnabledAsync(string feature) => evaluator.IsEnabledAsync(feature);

    public Task<bool> IsEnabledAsync<TContext>(string feature, TContext context) =>
        context is FeatureContext featureContext
            ? evaluator.IsEnabledAsync(feature, featureContext)
            : evaluator.IsEnabledAsync(feature);
}
