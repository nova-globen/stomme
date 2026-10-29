namespace Stomme.FeatureManagement.Application;

/// <summary>
/// Resolves a feature key's state from an external <em>configuration</em> source — a <c>features.json</c>,
/// an <c>appsettings</c> section, an environment variable, or (later, with no code change) Azure App
/// Configuration — independent of the per-tenant DB flag store. This is the config layer of the
/// precedence rule <b>config → DB → catalog default</b>: the evaluator consults this first and, when it
/// returns a non-null value, that value overrides the admin toggle and the catalog default.
/// <para>
/// <b>Contract.</b> <see cref="GetConfiguredState"/> returns <c>true</c>/<c>false</c> when the key is declared
/// in configuration with a definite on/off state, and <c>null</c> when configuration has no opinion — meaning
/// "fall through to the DB flag and, absent that, the catalog default". A stock install that declares no
/// feature configuration therefore behaves byte-for-byte as before: every key resolves to <c>null</c> here.
/// </para>
/// </summary>
public interface IFeatureConfigurationSource
{
    /// <summary>The config-declared state for <paramref name="key"/>, or <c>null</c> when it is not configured.</summary>
    bool? GetConfiguredState(string key);
}

/// <summary>
/// The no-op source: configuration never overrides. Registered as the fallback when no <c>IConfiguration</c>
/// is present (a minimal host or a test that composes the module without a config root), so the evaluator's
/// precedence collapses to the historical DB → catalog-default behaviour.
/// </summary>
public sealed class NullFeatureConfigurationSource : IFeatureConfigurationSource
{
    public static readonly NullFeatureConfigurationSource Instance = new();

    private NullFeatureConfigurationSource()
    {
    }

    public bool? GetConfiguredState(string key) => null;
}
