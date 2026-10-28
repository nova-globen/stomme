using System.Collections.Generic;
using Stomme.Formats;
using Stomme.Models;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Modules.Formats
{
    /// <summary>
    /// The Formats module's <see cref="ModuleDefinition"/> (BL-94, D110): one admin page, <c>admin/formats</c>, placed
    /// through <see cref="ModuleDefinition.PageTemplates"/> (reconciled additively per site on every boot) for
    /// Administrators, and the two keys <c>formats:view</c> / <c>formats:manage</c> declared as site Permission rows
    /// through <see cref="PermissionKeys.Declare"/> - both default to Administrators.
    /// </summary>
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "Formats";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "Date, number, currency and locale display formats: the system presets and this site's own definitions.",
            Categories = "Admin",
            Version = "1.0.0",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "PolyForm-Noncommercial-1.0.0 OR PolyForm-Small-Business-1.0.0",
            PermissionNames = PermissionKeys.Declare(FormatPermissions.Declarations),
            PageTemplates = new List<PageTemplate>
            {
                new PageTemplate
                {
                    Name = ModuleName,
                    Parent = "",
                    Path = "admin/formats",
                    AliasName = "*",
                    Version = "*",
                    Order = 7,
                    Icon = "oi oi-text",
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
