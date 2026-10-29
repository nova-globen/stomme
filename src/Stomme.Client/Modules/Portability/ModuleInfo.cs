using Stomme.Models;
using Stomme.Portability;
using Stomme.Security;

namespace Stomme.Modules.Portability
{
    /// <summary>
    /// The portability module's definition.
    /// </summary>
    /// <remarks>
    /// It DECLARES the keys - which is what seeds them per site - and no page template: the one admin page is added by
    /// <c>AdminSiteTemplate</c> only while <c>Portability:Enabled</c> is on (the Data Export posture), so a stock install's
    /// admin menu is unchanged. <c>portability:export-own</c> defaults to Registered Users; the rest to Administrators.
    /// </remarks>
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "Portability";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "Moves site configuration between sites as versioned packages, and gives a person a copy of the data " +
                          "the site holds about them.",
            Categories = "Admin",
            Version = "1.0.0",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "PolyForm-Noncommercial-1.0.0 OR PolyForm-Small-Business-1.0.0",
            PermissionNames = PermissionKeys.Declare(PortabilityPermissions.Declarations),
        };
    }
}
