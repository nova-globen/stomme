using System.Collections.Generic;
using Stomme.Models;
using Stomme.Modules;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Application.Modules.Notes
{
    public class ModuleInfo : IModule
    {
        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = "Notes",
            Description = "Short notes kept per site.",
            Version = "1.0.0",
            PermissionNames = PermissionKeys.Declare(NotesPermissions.Declarations),
            PageTemplates = new List<PageTemplate>
            {
                new PageTemplate
                {
                    Name = "Notes",
                    Path = "notes",
                    AliasName = "*",
                    Version = "*",
                    Order = 10,
                    IsNavigation = true,
                    PermissionList = new List<Permission>
                    {
                        new Permission(PermissionNames.View, RoleNames.Registered, true),
                        new Permission(PermissionNames.View, RoleNames.Admin, true),
                        new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                    },
                    PageTemplateModules = new List<PageTemplateModule>
                    {
                        new PageTemplateModule
                        {
                            Title = "Notes",
                            Pane = PaneNames.Default,
                            Order = 1,
                            PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.View, RoleNames.Registered, true),
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
