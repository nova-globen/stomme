using System.Collections.Generic;
using Stomme.Connectivity;
using Stomme.Models;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Modules.Connectivity
{
    /// <summary>
    /// The Connectivity module's <see cref="ModuleDefinition"/> (BL-94, D110): one admin page, <c>admin/connections</c>,
    /// placed through <see cref="ModuleDefinition.PageTemplates"/> (reconciled additively per site on every boot) for
    /// Administrators, and the two keys <c>connections:view</c> / <c>connections:manage</c> declared as site Permission
    /// rows through <see cref="PermissionKeys.Declare"/> - both default to Administrators.
    /// </summary>
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "Connectivity";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "Connections to external systems through installed connectors: configured, tested and used by pipelines, with every credential in the site's secret store.",
            Categories = "Admin",
            Version = "1.0.0",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "PolyForm-Noncommercial-1.0.0 OR PolyForm-Small-Business-1.0.0",
            PermissionNames = PermissionKeys.Declare(ConnectionPermissions.Declarations),
            PageTemplates = new List<PageTemplate>
            {
                new PageTemplate
                {
                    Name = "Connections",
                    Parent = "",
                    Path = "admin/connections",
                    AliasName = "*",
                    Version = "*",
                    Order = 8,
                    Icon = "oi oi-link-intact",
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
                            Title = "Connections",
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
