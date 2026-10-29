using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stomme.FeatureManagement.Application;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// The bundled-module enablement knob (Wave 18 / W2). A Stomme app can ship an external module by
    /// build-time <c>PackageReference</c> — the module's <c>.Stomme</c>-named assembly bakes into the publish
    /// output and is discovered at startup by the existing convention scan (<c>InstallationManager</c> /
    /// <c>IsStommeAssembly</c>), exactly as an <c>InstallationManager</c>-installed <c>.nupkg</c> is. Discovery
    /// is therefore UNCONDITIONAL and a stock install is byte-for-byte unaffected; what a config flag gates is
    /// whether that discovered module is SEEDED onto a page at install time (and thus whether it renders).
    /// <para>
    /// The key is <c>module.&lt;id&gt;.enabled</c> (the M5 knob), read through W1's
    /// <see cref="IFeatureConfigurationSource"/> — the config layer of the D84 precedence <c>config → DB →
    /// catalog default</c> — from the <c>FeatureManagement</c> section of <c>features.json</c> / an
    /// <c>appsettings</c> section / an environment variable / (later) Azure App Configuration. This deliberately
    /// consults the config source DIRECTLY rather than the full <see cref="IFeatureEvaluator"/>: the seeding
    /// decision runs before any per-tenant DB flag row exists, and its default when config is silent must be the
    /// historical "seed it" behaviour, so it keys off a DEFINITE config opinion only — <c>false</c> suppresses,
    /// <c>true</c> or absent (<c>null</c>) seeds as before. (The evaluator conflates absent-config-and-no-DB-row
    /// into <c>false</c>, which is the correct answer for a runtime capability check but the WRONG default for
    /// install-time seeding.)
    /// </para>
    /// </summary>
    /// <summary>A module assembly whose hosted services this host did not register (<c>module.&lt;id&gt;.enabled=false</c>).</summary>
    public sealed record SwitchedOffModuleAssembly(string AssemblyName, IReadOnlyList<string> SwitchIds);

    public static class ModuleFeatureFlags
    {
        public const string KeyPrefix = "module.";
        public const string KeySuffix = ".enabled";

        /// <summary>
        /// The <c>module.&lt;id&gt;.enabled</c> feature key for a module, where <c>&lt;id&gt;</c> is the module's
        /// display name lower-cased with whitespace removed (e.g. <c>"Blogs"</c> → <c>module.blogs.enabled</c>,
        /// <c>"HtmlText"</c> → <c>module.htmltext.enabled</c>). This is the human-facing knob a downstream app
        /// writes in <c>features.json</c>.
        /// </summary>
        public static string FeatureKey(string moduleName)
        {
            return $"{KeyPrefix}{Normalize(moduleName)}{KeySuffix}";
        }

        /// <summary>
        /// True only when configuration carries a DEFINITE OFF opinion for the module — the single condition that
        /// suppresses seeding. An unnamed module (config silent, <c>null</c>) or an explicit <c>true</c> both seed
        /// exactly as the historical pipeline did, so a stock install with no feature configuration is unchanged.
        /// </summary>
        public static bool IsDisabledByConfig(IFeatureConfigurationSource source, string moduleName)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                return false;
            }

            return source.GetConfiguredState(FeatureKey(moduleName)) == false;
        }

        /// <summary>
        /// The switch ids of a module assembly (M2, ruling C33): its <see cref="Stomme.Modules.ModuleSwitchAttribute"/>
        /// ids, or, without one, the names of the modules it declares. Empty for an assembly that is neither - the host's
        /// own and the framework's, which no switch can turn off.
        /// </summary>
        public static IReadOnlyList<string> SwitchIds(Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(assembly);
            var declared = assembly.GetCustomAttributes<Stomme.Modules.ModuleSwitchAttribute>().Select(attribute => attribute.Id).ToList();
            if (declared.Count > 0)
            {
                return declared;
            }

            try
            {
                return assembly.GetInstances<Stomme.Modules.IModule>().Select(module => module.ModuleDefinition?.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name)).ToList();
            }
            catch (Exception)
            {
                return Array.Empty<string>(); // an assembly whose types cannot be listed declares no module we can name
            }
        }

        /// <summary>
        /// True when configuration switches a module assembly OFF on this host: it has switch ids and EVERY one of them is
        /// <c>module.&lt;id&gt;.enabled=false</c>. The host then registers none of its hosted services and calls none of its
        /// endpoint contributors. Silent configuration, or <c>true</c>, leaves it on - a stock host is unchanged.
        /// </summary>
        public static bool IsSwitchedOff(IFeatureConfigurationSource source, Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(source);
            var ids = SwitchIds(assembly);
            return ids.Count > 0 && ids.All(id => IsDisabledByConfig(source, id));
        }

        private static string Normalize(string moduleName)
        {
            return (moduleName ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", string.Empty);
        }
    }
}
