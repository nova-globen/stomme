using Stomme.Models;

namespace Stomme.Modules.Backup
{
    /// <summary>
    /// The backup module's definition (BL-81, D106 item 9).
    /// </summary>
    /// <remarks>
    /// It declares NO permission keys, on purpose (refutation 8): <c>PermissionKeys.Declare</c> always admits Administrators,
    /// and an archive holds every site's users and password hashes - so everything is the Host's, by role. The one admin page
    /// is added by <c>AdminSiteTemplate</c> only while <c>Backup:Enabled</c> is on.
    /// </remarks>
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "Backup";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "Takes encrypted snapshots of the whole installation and restores one when the host restarts.",
            Categories = "Admin",
            Version = "1.0.0",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "PolyForm-Noncommercial-1.0.0 OR PolyForm-Small-Business-1.0.0",
        };
    }
}
