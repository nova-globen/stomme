using Stomme.Export;
using Stomme.Models;
using Stomme.Security;

namespace Stomme.Modules.DataExport
{
    /// <summary>
    /// The data-export module's definition (D108 items 1 and 9).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is where the permission keys are DECLARED, and the declaration is what seeds them.</b> A site's
    /// <c>export:*</c> Permission rows come from <see cref="ModuleDefinition.PermissionNames"/>; without this type the
    /// server half compiles, every <c>[Requires]</c> names a key no site ever gets, and the feature is unreachable -
    /// which is exactly what <c>PermissionKeyDeclarationGateTests</c> refused to let through.
    /// </para>
    /// <para>
    /// It declares NO page template. The one page is added by <c>AdminSiteTemplate</c> only while
    /// <c>DataExport:Enabled</c> is on, so a stock install's admin menu is unchanged; the keys, harmless on their own,
    /// are seeded either way and default to Administrators.
    /// </para>
    /// </remarks>
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "Data Export";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "Exports tabular data to CSV, Excel, JSON or PDF as background jobs, with each artifact kept " +
                          "in a private, administrators-only folder until it expires.",
            Categories = "Admin",
            Version = "1.0.0",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "PolyForm-Noncommercial-1.0.0 OR PolyForm-Small-Business-1.0.0",
            PermissionNames = PermissionKeys.Declare(ExportPermissions.Declarations),
        };
    }
}
