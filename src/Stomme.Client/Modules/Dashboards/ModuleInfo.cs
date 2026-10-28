using System.Collections.Generic;
using Stomme.Dashboards;
using Stomme.Models;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Modules.Dashboards
{
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "Dashboards";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "Dashboards of widgets bound to the site's datasets: charts over ECharts, grids, lists and KPIs on a layout grid, published once and cloned as drafts.",
            Categories = "Admin",
            Version = "1.0.0",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "PolyForm-Noncommercial-1.0.0 OR PolyForm-Small-Business-1.0.0",
            PermissionNames = PermissionKeys.Declare(DashboardPermissions.Declarations),
            PageTemplates = new List<PageTemplate>
            {
                new PageTemplate
                {
                    Name = "Dashboards",
                    Parent = "",
                    Path = "admin/dashboards",
                    AliasName = "*",
                    Version = "*",
                    Order = 8,
                    Icon = "oi oi-dashboard",
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
                            Title = "Dashboards",
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
