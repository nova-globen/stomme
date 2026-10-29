using System.Collections.Generic;
using Stomme.Metering;
using Stomme.Models;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Modules.Metering
{
    /// <summary>
    /// The Metering module's <see cref="ModuleDefinition"/> - how the framework discovers it, what it
    /// declares to the authorization bridge, and where it lands on a fresh site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Permissions.</b> <see cref="ModuleDefinition.PermissionNames"/> is built ONLY by the per-key
    /// <c>PermissionKeys.Declare</c> - never by hand, since a bare two-segment key is silently ignored (BL-99 F2).
    /// The rows seed on install and reconcile lazily through <c>ModuleDefinitionRepository</c>; a revocation is an
    /// Administrators DENY row, never zero rows, because zero rows re-seed.
    /// </para>
    /// <para>
    /// <b>Pages - and why NOT <c>AdminSiteTemplate</c>.</b> The design said "AdminSiteTemplate 33 to 36", written
    /// before BL-78 landed. Its sibling module places itself through <see cref="ModuleDefinition.PageTemplates"/>,
    /// which the framework reconciles additively per site on every boot, and which keeps the pages the MODULE's
    /// rather than the framework's - so a site that never enables metering never acquires framework admin pages for
    /// it. That is the shape used here: <c>usage</c> for every registered user and <c>admin/usage</c> for the
    /// operator, with My usage / Quotas / Usage log as module ACTIONS reached from the entry control. Both templates
    /// carry <c>AliasName = "*"</c>: <c>CreatePagesAsync</c> skips a template whose AliasName is empty when a real
    /// alias is passed.
    /// </para>
    /// </remarks>
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "Metering";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "Usage metering and quotas: per-site consumption, calendar-aligned allowances, the usage " +
                          "ledger and the AI usage dashboard.",
            Categories = "Admin",
            Version = "1.0.0",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "PolyForm-Noncommercial-1.0.0 OR PolyForm-Small-Business-1.0.0",
            PermissionNames = PermissionKeys.Declare(MeteringPermissions.Declarations),
            PageTemplates = new List<PageTemplate>
            {
                new PageTemplate
                {
                    Name = "Usage",
                    Parent = "",
                    Path = "usage",
                    AliasName = "*",
                    Version = "*",
                    Order = 10,
                    Icon = "oi oi-pulse",
                    IsNavigation = false,
                    IsPersonalizable = false,
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
                            Title = ModuleName,
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
                },
                new PageTemplate
                {
                    Name = "Usage",
                    Parent = "",
                    Path = "admin/usage",
                    AliasName = "*",
                    Version = "*",
                    Order = 2,
                    Icon = "oi oi-pulse",
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
