using System.Collections.Generic;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Modules.Erasure
{
    /// <summary>
    /// The erasure module's <see cref="ModuleDefinition"/> (BL-116 increment 4, D112): one Host-only admin page,
    /// <c>admin/erasure</c>, listing the site's erasure receipts - what an operator shows a supervisory authority - with
    /// a retry for a failed run. Placed through <see cref="ModuleDefinition.PageTemplates"/> and reconciled additively
    /// per site on every boot, so an install that predates the module gets the page.
    /// </summary>
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "Erasure";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "The right-to-erasure receipts of this site: what was deleted, anonymised or retained for whom, without naming anyone.",
            Categories = "Admin",
            Version = "1.0.0",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "PolyForm-Noncommercial-1.0.0 OR PolyForm-Small-Business-1.0.0",
            PageTemplates = new List<PageTemplate>
            {
                new PageTemplate
                {
                    Name = ModuleName,
                    Parent = "",
                    Path = "admin/erasure",
                    AliasName = "*",
                    Version = "*",
                    Order = 6,
                    Icon = "oi oi-shield",
                    IsNavigation = false,
                    IsPersonalizable = false,
                    PermissionList = new List<Permission>
                    {
                        new Permission(PermissionNames.View, RoleNames.Host, true),
                        new Permission(PermissionNames.Edit, RoleNames.Host, true)
                    },
                    PageTemplateModules = new List<PageTemplateModule>
                    {
                        new PageTemplateModule
                        {
                            Title = "Erasure receipts",
                            Pane = PaneNames.Default,
                            Order = 1,
                            PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.View, RoleNames.Host, true),
                                new Permission(PermissionNames.Edit, RoleNames.Host, true)
                            }
                        }
                    }
                }
            }
        };
    }
}
