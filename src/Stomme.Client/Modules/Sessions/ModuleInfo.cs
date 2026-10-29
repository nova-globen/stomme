using System.Collections.Generic;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Modules.Sessions
{
    /// <summary>
    /// The sessions module's <see cref="ModuleDefinition"/>: one admin page, <c>admin/sessions</c>, listing every
    /// signed-in session of the site, placed through <see cref="ModuleDefinition.PageTemplates"/> and reconciled
    /// additively per site on every boot. A user's OWN sessions are listed on their profile (<see cref="MySessions"/>).
    /// </summary>
    /// <remarks>
    /// <b>No permission keys.</b> Who may end whose sign-in is the framework's question - the Administrators role, decided
    /// again by the server on every call - and <c>identity.*</c> is the prefix D109 reserves.
    /// </remarks>
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "Sessions";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "Every signed-in session on this site, each one revocable on its own.",
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
                    Path = "admin/sessions",
                    AliasName = "*",
                    Version = "*",
                    Order = 5,
                    Icon = "oi oi-monitor",
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
