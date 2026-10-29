using System.Collections.Generic;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Modules.IdentityAudit
{
    /// <summary>
    /// The identity audit module's <see cref="ModuleDefinition"/>: one admin page, <c>admin/identity-audit</c>,
    /// placed through <see cref="ModuleDefinition.PageTemplates"/> and reconciled additively per site on every boot.
    /// </summary>
    /// <remarks>
    /// <b>No permission keys.</b> Reading a site's account record is the framework's question - the Administrators role,
    /// decided again by the controller on every call - and <c>identity.*</c> is the prefix the framework reserves for identity permissions.
    /// </remarks>
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "Identity Audit";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "The identity audit ledger: sign-ins, lockouts, password resets, account and role changes on this site.",
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
                    Path = "admin/identity-audit",
                    AliasName = "*",
                    Version = "*",
                    Order = 4,
                    Icon = "oi oi-shield",
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
