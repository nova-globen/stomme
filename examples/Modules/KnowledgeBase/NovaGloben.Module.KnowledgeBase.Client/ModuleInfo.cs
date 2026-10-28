using System.Collections.Generic;
using Stomme.Models;
using Stomme.Modules;
using Stomme.Security;
using Stomme.Shared;

namespace NovaGloben.Module.KnowledgeBase
{
    /// <summary>
    /// The module definition the host discovers by convention. It declares the module's permission KEYS with the roles a
    /// new site grants them to (D99), names the Shared assembly a WebAssembly site must also load, and places the module
    /// on a public page, and the package's categories module on a page nested under it, through
    /// <see cref="ModuleDefinition.PageTemplates"/> (<c>AliasName = "*"</c> - the default empty alias never matches, so a
    /// template without it is silently skipped).
    /// </summary>
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "Knowledge Base";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "The Stomme exemplar module: a site's knowledge base - articles in categories, drafted and published by its members - built to show every extension point a module can use.",
            Categories = "Common",
            Version = "1.0.0",
            Dependencies = "NovaGloben.Module.KnowledgeBase.Shared.Stomme",
            PackageName = "NovaGloben.Module.KnowledgeBase",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "MIT",
            // The SERVER MANAGER the host creates wherever it asks a placed module for something - the site search's index
            // job (ISearchable) and the sitemap (ISitemap). A name, not a type: this assembly never references the Server one.
            ServerManagerType = "NovaGloben.Module.KnowledgeBase.Manager.KnowledgeBaseManager, NovaGloben.Module.KnowledgeBase.Server.Stomme",
            PermissionNames = PermissionKeys.Declare(KnowledgeBasePermissions.Declarations),
            PageTemplates = new List<PageTemplate>
            {
                new PageTemplate
                {
                    Name = "Knowledge Base",
                    Parent = "",
                    Path = "knowledge-base",
                    AliasName = "*",
                    Version = "*",
                    Order = 8,
                    Icon = "oi oi-book",
                    IsNavigation = true,
                    IsPersonalizable = false,
                    PermissionList = new List<Permission>
                    {
                        new Permission(PermissionNames.View, RoleNames.Everyone, true),
                        new Permission(PermissionNames.View, RoleNames.Admin, true),
                        new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                    },
                    PageTemplateModules = new List<PageTemplateModule>
                    {
                        new PageTemplateModule
                        {
                            Title = "Knowledge Base",
                            Pane = PaneNames.Default,
                            Order = 1,
                            PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.View, RoleNames.Everyone, true),
                                new Permission(PermissionNames.View, RoleNames.Admin, true),
                                new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                            }
                        }
                    }
                },

                // The NESTED page: a child of the page above (Parent is the parent's path), carrying the package's SECOND
                // module definition. Declared here, after its parent, so the parent always exists when it is created.
                new PageTemplate
                {
                    Name = "Categories",
                    Parent = "knowledge-base",
                    Path = "knowledge-base/categories",
                    AliasName = "*",
                    Version = "*",
                    Order = 1,
                    Icon = "oi oi-folder",
                    IsNavigation = true,
                    IsPersonalizable = false,
                    PermissionList = new List<Permission>
                    {
                        new Permission(PermissionNames.View, RoleNames.Everyone, true),
                        new Permission(PermissionNames.View, RoleNames.Admin, true),
                        new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                    },
                    PageTemplateModules = new List<PageTemplateModule>
                    {
                        new PageTemplateModule
                        {
                            ModuleDefinitionName = Categories.ModuleInfo.ModuleDefinitionName,
                            Title = "Categories",
                            Pane = PaneNames.Default,
                            Order = 1,
                            PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.View, RoleNames.Everyone, true),
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
