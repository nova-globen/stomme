using System.Collections.Generic;
using Stomme.Models;
using Stomme.Notifications;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Modules.Notifications
{
    /// <summary>
    /// The Notifications module's <see cref="ModuleDefinition"/> (BL-78 step 5) - how the framework discovers it, what it
    /// declares to the authorization bridge, and where it lands on a fresh site.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Permissions.</b> <see cref="ModuleDefinition.PermissionNames"/> is built ONLY by the per-key
    /// <c>PermissionKeys.Declare</c> (never by hand - a bare two-segment key is silently ignored, BL-99 F2): <c>View,Edit</c>
    /// first, then the five SPLIT keys with their default roles (<see cref="NotificationsPermissions.Declarations"/>). The rows
    /// seed on install and reconcile lazily through the existing <c>ModuleDefinitionRepository</c>; a revocation is an
    /// Administrators DENY row, never zero rows (zero rows re-seed - BL-99 correction 7).
    /// </para>
    /// <para>
    /// <b>Pages.</b> A page places the module and renders its <c>Index</c>; the other controls (<c>Preferences</c>,
    /// <c>Templates</c>, <c>Channels</c>, <c>Log</c>) are module ACTIONS reached from it. So the user-facing page is
    /// <c>notifications</c> (Registered Users) and the operator's is <c>admin/notifications</c> (Administrators); each control
    /// gates itself on its key through <c>IsAuthorizedPermission</c>, and the server decides again per request. Both templates
    /// carry <c>AliasName = "*"</c>: <c>CreatePagesAsync</c> skips a template whose AliasName is empty when a real alias is passed.
    /// </para>
    /// <para>
    /// <b>Configuration.</b> <see cref="ModuleDefinition.ConfigSettingsType"/> is the <c>Settings</c> control (the site's
    /// external-recipient opt-in, request retention and webhook host allowlist).
    /// </para>
    /// </remarks>
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "Notifications";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "Multi-channel notifications (e-mail, in-app, webhook): the notification center, per-user preferences, " +
                          "Liquid templates, delivery channels and the send log.",
            Categories = "Admin",
            Version = "1.0.0",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "PolyForm-Noncommercial-1.0.0 OR PolyForm-Small-Business-1.0.0",
            PermissionNames = PermissionKeys.Declare(NotificationsPermissions.Declarations),
            ConfigSettingsType = "Stomme.Modules.Notifications.Settings, Stomme.Client",
            PageTemplates = new List<PageTemplate>
            {
                new PageTemplate
                {
                    Name = "Notifications",
                    Parent = "",
                    Path = "notifications",
                    AliasName = "*",
                    Version = "*",
                    Order = 9,
                    Icon = "oi oi-bell",
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
                    Name = "Notifications",
                    Parent = "",
                    Path = "admin/notifications",
                    AliasName = "*",
                    Version = "*",
                    Order = 1,
                    Icon = "oi oi-bell",
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