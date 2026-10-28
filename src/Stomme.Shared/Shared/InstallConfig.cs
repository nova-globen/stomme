
using System.Collections.Generic;

namespace Stomme.Shared
{
    public class InstallConfig
    {
        public string ConnectionString { get; set; }
        public string DatabaseType { get; set; }
        public string Aliases { get; set; }
        public string TenantName { get; set; }
        public bool IsNewTenant { get; set; }
        public string SiteName { get; set; }
        public string HostUsername { get; set; }
        public string HostPassword { get; set; }
        public string HostEmail { get; set; }
        public string HostName { get; set; }
        public string SiteTemplate { get; set; }
        public string DefaultTheme { get; set; }
        public string DefaultContainer { get; set; }
        public string DefaultAdminContainer { get; set; }
        public string RenderMode { get; set; }
        public string Runtime { get; set; }

        /// <summary>
        /// Optional prerender override (BL-57). Empty/null keeps the historical default — prerender ON for
        /// Interactive render mode. Set to <c>"false"</c> to install an Interactive/WebAssembly site with
        /// prerender OFF, which is required behind a reverse proxy whose container cannot resolve its own public
        /// host (the SSR prerender pass would HTTP-call the app's public URL and fail — every page an empty 200).
        /// </summary>
        public string Prerender { get; set; }

        /// <summary>
        /// Module-wide configuration collected by the installer's onboarding step (D86), written to the master
        /// <c>EntityNames.ModuleDefinition</c> settings channel AFTER the install creates the module definitions.
        /// Collected pre-install (the master store does not exist yet) and persisted server-side in the trusted
        /// install path (the wizard never logs in, so an authorized HTTP settings write is not available). Empty
        /// for a stock/automated install, so this is byte-for-byte unchanged when unused.
        /// </summary>
        public List<ModuleConfigSetting> ModuleSettings { get; set; }
    }

    /// <summary>
    /// One module's install-time configuration: a set of <c>SettingName → SettingValue</c> pairs keyed to a
    /// module by its <see cref="Models.ModuleDefinition.ModuleDefinitionName"/> (the stable identifier that
    /// survives from the installer's assembly scan to the master settings write).
    /// </summary>
    public class ModuleConfigSetting
    {
        public string ModuleDefinitionName { get; set; }
        public Dictionary<string, string> Settings { get; set; }
    }
}
