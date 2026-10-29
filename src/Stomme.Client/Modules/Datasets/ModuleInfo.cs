using System.Collections.Generic;
using Stomme.Datasets;
using Stomme.Models;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Modules.Datasets
{
    /// <summary>
    /// The Datasets module's <see cref="ModuleDefinition"/>: one admin page, <c>admin/datasets</c>, placed
    /// through <see cref="ModuleDefinition.PageTemplates"/> (reconciled additively per site on every boot) for
    /// Administrators, and the three keys <c>datasets:view</c> / <c>datasets:manage</c> / <c>datasets:preview</c> declared
    /// as site Permission rows through <see cref="PermissionKeys.Declare"/> - all default to Administrators.
    /// </summary>
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "Datasets";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "Schema-aware datasets registered by pipelines, extraction and uploads: a reference to the origin, an inferred schema, a masked summary and a bounded, classification-gated preview.",
            Categories = "Admin",
            Version = "1.0.0",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "PolyForm-Noncommercial-1.0.0 OR PolyForm-Small-Business-1.0.0",
            PermissionNames = PermissionKeys.Declare(DatasetPermissions.Declarations),
            PageTemplates = new List<PageTemplate>
            {
                new PageTemplate
                {
                    Name = ModuleName,
                    Parent = "",
                    Path = "admin/datasets",
                    AliasName = "*",
                    Version = "*",
                    Order = 9,
                    Icon = "oi oi-spreadsheet",
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
