using Stomme.Help;
using Stomme.Models;
using Stomme.Security;

namespace Stomme.Modules.ContextualHelp
{
    /// <summary>
    /// Contextual help's <see cref="ModuleDefinition"/> (BL-84, D103 items 8-9) - how the framework discovers it and what
    /// it declares to the authorization bridge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Permissions, per key</b> (D103 refutation 15): <c>help:view</c> to Everyone, <c>help:author</c> and
    /// <c>help:manage</c> to Administrators. The UNIFORM <c>Declare</c> would have handed all three to Everyone, and
    /// <c>UserSecurity.IsAuthorized</c> admits All Users at userId -1 - an anonymous visitor could author and manage.
    /// </para>
    /// <para>
    /// <b>No page template here.</b> The admin page is the framework's <c>admin/help</c>, placed by
    /// <c>AdminSiteTemplate</c> ONLY while <c>Help:Enabled</c> is on and reconciled onto existing sites at boot. A page
    /// template on the definition would give every stock install a help page for a feature that is off.
    /// </para>
    /// <para>
    /// The name is what the D85 knob is keyed on: "Contextual Help" becomes <c>module.contextualhelp.enabled</c>.
    /// </para>
    /// </remarks>
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "Contextual Help";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "In-app help keyed to the page and the controls on it, contributed by modules and edited per site.",
            Categories = "Admin",
            Version = "1.0.0",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "PolyForm-Noncommercial-1.0.0 OR PolyForm-Small-Business-1.0.0",
            PermissionNames = PermissionKeys.Declare(HelpPermissions.Declarations),
        };
    }
}
