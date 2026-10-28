using System.Collections.Generic;
using Stomme.Health;
using Stomme.Models;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Modules.Health
{
    /// <summary>
    /// The Health module's <see cref="ModuleDefinition"/> (D104 item 12) — how the framework discovers it, what it
    /// declares to the authorization bridge, and where it lands on a fresh site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Permissions.</b> <see cref="ModuleDefinition.PermissionNames"/> is built ONLY by the per-key
    /// <c>PermissionKeys.Declare</c> — never by hand, since a bare two-segment key is silently ignored (BL-99 F2).
    /// This declaration is not paperwork: BL-99's reflective gate refused this row's <c>[Requires]</c> attributes
    /// until it existed, on the ground that a key no definition declares seeds NO permission row and therefore
    /// denies everyone. The gate was right, and it caught it before anything shipped.
    /// </para>
    /// <para>
    /// <b>One page, admin-only.</b> Unlike metering there is no public counterpart: health has no self-service half
    /// — a visitor has no "my health" — so the module places a single <c>admin/health</c> page through
    /// <see cref="ModuleDefinition.PageTemplates"/>, which the framework reconciles additively per site on every
    /// boot. That keeps the page the MODULE's rather than the framework's, so a site that never wants it does not
    /// acquire a framework admin page for it. <c>AliasName = "*"</c> is required: <c>CreatePagesAsync</c> skips a
    /// template whose AliasName is empty when a real alias is passed.
    /// </para>
    /// </remarks>
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "Health";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "Operational health: dependency probes, availability history and an alert ledger for the " +
                          "things this site depends on.",
            Categories = "Admin",
            Version = "1.0.0",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "PolyForm-Noncommercial-1.0.0 OR PolyForm-Small-Business-1.0.0",
            PermissionNames = PermissionKeys.Declare(HealthPermissions.Declarations),
            PageTemplates = new List<PageTemplate>
            {
                new PageTemplate
                {
                    Name = "Health",
                    Parent = "",
                    Path = "admin/health",
                    AliasName = "*",
                    Version = "*",
                    Order = 3,
                    Icon = "oi oi-heart",
                    IsNavigation = false,
                    IsPersonalizable = false,
                    PermissionList = new List<Permission>
                    {
                        new Permission(PermissionNames.View, RoleNames.Admin, true),
                        new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                    },
                    PageTemplateModules = new List<PageTemplateModule>
                    {
                        new PageTemplateModule
                        {
                            Title = ModuleName,
                            Pane = PaneNames.Default,
                            Order = 1,
                            PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.View, RoleNames.Admin, true),
                                new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                            }
                        }
                    }
                }
            }
        };
    }
}
