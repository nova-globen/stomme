using Stomme.FeatureManagement.Application;
using Microsoft.Extensions.Configuration;

namespace Stomme.FeatureManagement.Infrastructure;

/// <summary>
/// The <see cref="IFeatureConfigurationSource"/> backed by <see cref="IConfiguration"/> (D84). It reads flags
/// from the <c>FeatureManagement</c> section — <b>Microsoft.FeatureManagement's own default section name</b> —
/// so the shape a <c>features.json</c> author writes is the same shape Microsoft's config provider expects,
/// and adding <c>builder.Configuration.AddAzureAppConfiguration(o =&gt; o.UseFeatureFlags())</c> later feeds
/// this source with <b>no code change</b>: Azure App Configuration surfaces its feature flags into
/// <see cref="IConfiguration"/> under exactly this section.
/// <para>
/// <b>Reading, not evaluating.</b> This resolves a key to a <em>definite</em> on/off; it deliberately does not
/// run Microsoft's client filters (percentage / time-window / targeting). Stomme owns richer targeting
/// (per-tenant role allow-lists, deterministic rollout, per-user overrides, licence entitlement, flag
/// dependencies) in its own evaluator, so a config entry that carries filters is treated as "no definite
/// opinion" (<c>null</c>) and Stomme's targeting decides. The forms honoured:
/// </para>
/// <list type="bullet">
///   <item>A boolean literal — <c>"module.blogs.enabled": true</c> — the form a hand-authored file, an
///   environment variable (<c>FeatureManagement__module.blogs.enabled=true</c>), and a simple Azure App
///   Configuration on/off toggle all produce.</item>
///   <item>The Microsoft object form's explicit enabled bit — <c>{ "enabled": false }</c> (schema v2) — and
///   an empty <c>EnabledFor</c> array (schema v1), which Microsoft defines as an explicitly-off flag.</item>
/// </list>
/// </summary>
/// <summary>
/// The configuration-backed source for a caller that runs before any container exists - a host deciding at composition
/// time which module assemblies to start (<c>module.&lt;id&gt;.enabled</c>, ruling C33). The same reading as the
/// registered source.
/// </summary>
public static class FeatureConfigurationSources
{
    public static IFeatureConfigurationSource FromConfiguration(IConfiguration configuration) =>
        new ConfigurationFeatureConfigSource(configuration);
}

internal sealed class ConfigurationFeatureConfigSource : IFeatureConfigurationSource
{
    /// <summary>Microsoft.FeatureManagement's default configuration section name.</summary>
    public const string DefaultSectionName = "FeatureManagement";

    private readonly IConfiguration _configuration;
    private readonly string _sectionName;

    public ConfigurationFeatureConfigSource(IConfiguration configuration, string? sectionName = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
        _sectionName = string.IsNullOrWhiteSpace(sectionName) ? DefaultSectionName : sectionName!.Trim();
    }

    public bool? GetConfiguredState(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var section = _configuration.GetSection(_sectionName).GetSection(key.Trim());
        if (!section.Exists())
        {
            return null;
        }

        // Simple form: a boolean literal. This is what a features.json author, an environment variable, and a
        // simple Azure App Configuration toggle all produce, and the M5 "module.<id>.enabled" knob.
        if (section.Value is { } literal)
        {
            return bool.TryParse(literal, out var value) ? value : null;
        }

        // Microsoft.FeatureManagement object form. Honour only the parts that resolve to a definite on/off
        // without running client filters:
        //   schema v2:  { "enabled": true/false, "conditions": { ... } }
        var enabled = section["enabled"] ?? section["Enabled"];
        if (enabled is not null && bool.TryParse(enabled, out var enabledValue))
        {
            return enabledValue;
        }

        //   schema v1:  { "EnabledFor": [ ... ] } — Microsoft treats an EMPTY list as an explicitly-off flag;
        //   a non-empty list carries client filters this source does not evaluate, so it defers to Stomme.
        var enabledFor = section.GetSection("EnabledFor");
        if (enabledFor.Exists() && !enabledFor.GetChildren().Any())
        {
            return false;
        }

        // An object form carrying filters we do not evaluate: config has no definite opinion; Stomme's own
        // targeting (the DB flag) decides.
        return null;
    }
}
