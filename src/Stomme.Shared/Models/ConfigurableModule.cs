using Stomme.Documentation;

namespace Stomme.Models
{
    /// <summary>
    /// A bundled, enabled module that declares a module-wide config control (D86). Returned by the install-time
    /// discovery endpoint so the installer wizard's onboarding step can render each module's
    /// <see cref="ConfigSettingsType"/> control while the app is still uninstalled. Deliberately a thin projection
    /// (name + config-control type) — the master settings store does not exist yet, so no values travel here.
    /// </summary>
    [PrivateApi("Install-time discovery projection")]
    public class ConfigurableModule
    {
        /// <summary>The module's friendly name (for the onboarding UI heading).</summary>
        public string Name { get; set; }

        /// <summary>
        /// The stable module identifier — matches the <see cref="ModuleDefinition.ModuleDefinitionName"/> the
        /// install pipeline persists, so it is the key under which onboarding config is written to the master store.
        /// </summary>
        public string ModuleDefinitionName { get; set; }

        /// <summary>The assembly-qualified type name of the module-wide <c>ISettingsControl</c> to render.</summary>
        public string ConfigSettingsType { get; set; }
    }
}
