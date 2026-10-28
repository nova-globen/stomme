using Stomme.Models;
using Stomme.Pipelines;
using Stomme.Security;

namespace Stomme.Modules.Pipelines
{
    /// <summary>
    /// The Pipelines module (BL-95, D111): pipelines authored and run on the vendored engine, per site. The module carries
    /// no page template of its own: its admin page (admin/pipelines) is placed by the admin site template only while
    /// Pipelines:Enabled is on, so a stock install shows nothing of it. Every permission key defaults to Administrators.
    /// </summary>
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "Pipelines";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "Pipelines: versioned, publishable pipelines run by the engine, with their runs and execution logs.",
            Categories = "Admin",
            Version = "1.0.0",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "PolyForm-Noncommercial-1.0.0 OR PolyForm-Small-Business-1.0.0",
            PermissionNames = PermissionKeys.Declare(PipelinesPermissions.Declarations)
        };
    }
}
