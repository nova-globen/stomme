using System.Collections.Generic;
using Stomme.CloudUsage;
using Stomme.Models;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Modules.CloudUsage
{
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "Cloud Usage";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "The site's cloud and SaaS spend in one schema: synced through its cost connections, rolled up per currency, measured against budgets, alerted on thresholds and spikes.",
            Categories = "Admin",
            Version = "1.0.0",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "PolyForm-Noncommercial-1.0.0 OR PolyForm-Small-Business-1.0.0",
            PermissionNames = PermissionKeys.Declare(CloudUsagePermissions.Declarations),
            PageTemplates = new List<PageTemplate>
            {
                new PageTemplate
                {
                    Name = "Cloud Usage",
                    Parent = "",
                    Path = "admin/cloud-usage",
                    AliasName = "*",
                    Version = "*",
                    Order = 9,
                    Icon = "oi oi-cloud",
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
                            Title = "Cloud Usage",
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
