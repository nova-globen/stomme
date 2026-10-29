using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Stomme.Documentation;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Infrastructure.SiteTemplates
{
    [PrivateApi("Mark Site-Template classes as private, since it's not very useful in the public docs")]
    public class AdminSiteTemplate : ISiteTemplate
    {
        private readonly IStringLocalizer<AdminSiteTemplate> _localizer;
        // IConfigurationRoot, NOT IConfiguration: AddStomme registers the fully-merged configuration (which
        // includes the appsettings.override.json used by the test harness) as IConfigurationRoot, so a plain
        // IConfiguration would resolve to the host's default config WITHOUT the override — and the opt-in page
        // would never seed under test. SiteRepository injects IConfigurationRoot for exactly this reason. (The
        // container's IConfiguration has the override file too, but still not features.json: the root stays the answer.)
        private readonly IConfigurationRoot _configuration;

        public AdminSiteTemplate(IStringLocalizer<AdminSiteTemplate> localizer, IConfigurationRoot configuration)
        {
            _localizer = localizer;
            _configuration = configuration;
        }

        public string Name
        {
            get { return "Admin Site Template"; } 
        }

        public Task<List<PageTemplate>> CreateSiteAsync(Site site)
        {
            var pageTemplates = new List<PageTemplate>();
            var seed = 1000; // order

            // user pages
            pageTemplates.Add(new PageTemplate
            {
                Name = "Login",
                Parent = "",
                Path = "login",
                Order = seed + 1,
                Icon = Icons.LockLocked,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.View, RoleNames.Admin, true),
                    new Permission(PermissionNames.View, RoleNames.Everyone, true),
                    new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                },
                PageTemplateModules = new List<PageTemplateModule>
                {
                    new PageTemplateModule
                    {
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Login.Index).ToModuleDefinitionName(), Title = "User Login", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.View, RoleNames.Everyone, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = ""
                    }
                }
            });

            pageTemplates.Add(new PageTemplate
            {
                Name = "Register",
                Parent = "",
                Path = "register",
                Order = seed + 3,
                Icon = Icons.Person,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.View, RoleNames.Admin, true),
                    new Permission(PermissionNames.View, RoleNames.Everyone, true),
                    new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                },
                PageTemplateModules = new List<PageTemplateModule>
                {
                    new PageTemplateModule
                    {
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Register.Index).ToModuleDefinitionName(), Title = "User Registration", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.View, RoleNames.Everyone, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = ""
                    }
                }
            });

            pageTemplates.Add(new PageTemplate
            {
                Name = "Reset",
                Parent = "",
                Path = "reset",
                Order = seed + 5,
                Icon = Icons.Person,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.View, RoleNames.Admin, true),
                    new Permission(PermissionNames.View, RoleNames.Everyone, true),
                    new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                },
                PageTemplateModules = new List<PageTemplateModule>
                {
                    new PageTemplateModule
                    {
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Reset.Index).ToModuleDefinitionName(), Title = "Password Reset", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.View, RoleNames.Everyone, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = ""
                    }
                }
            });

            pageTemplates.Add(new PageTemplate
            {
                Name = "Profile",
                Parent = "",
                Path = "profile",
                Order = seed + 7,
                Icon = Icons.Person,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.View, RoleNames.Admin, true),
                    new Permission(PermissionNames.View, RoleNames.Registered, true),
                    new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                },
                PageTemplateModules = new List<PageTemplateModule>
                {
                    new PageTemplateModule
                    {
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.UserProfile.Index).ToModuleDefinitionName(), Title = "User Profile", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.View, RoleNames.Registered, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = ""
                    }
                }
            });

            pageTemplates.Add(new PageTemplate
            {
                Name = "Search",
                Parent = "",
                Path = "search",
                Order = seed + 9,
                Icon = Icons.MagnifyingGlass,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission> {
                    new Permission(PermissionNames.View, RoleNames.Admin, true),
                    new Permission(PermissionNames.View, RoleNames.Everyone, true),
                    new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                },
                PageTemplateModules = new List<PageTemplateModule> {
                    new PageTemplateModule { ModuleDefinitionName = typeof(Stomme.Modules.Admin.SearchResults.Index).ToModuleDefinitionName(), Title = "Search", Pane = PaneNames.Default,
                        PermissionList = new List<Permission> {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.View, RoleNames.Everyone, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        }
                    }
                }
            });

            pageTemplates.Add(new PageTemplate
            {
                Name = "Privacy",
                Parent = "",
                Path = "privacy",
                Order = seed + 11,
                Icon = Icons.Eye,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission>
                    {
                        new Permission(PermissionNames.View, RoleNames.Everyone, true),
                        new Permission(PermissionNames.View, RoleNames.Admin, true),
                        new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                    },
                PageTemplateModules = new List<PageTemplateModule>
                    {
                        new PageTemplateModule { ModuleDefinitionName = "Stomme.Modules.HtmlText, Stomme.Client", Title = "Privacy Policy", Pane = PaneNames.Default,
                            PermissionList = new List<Permission> {
                                new Permission(PermissionNames.View, RoleNames.Everyone, true),
                                new Permission(PermissionNames.View, RoleNames.Admin, true),
                                new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                            },
                            Settings = new List<Setting> {
                                new Setting { SettingName = "DynamicTokens", SettingValue = "true" }
                            },
                            Content = _localizer["Privacy"]
                        }
                    }
            });

            pageTemplates.Add(new PageTemplate
            {
                Name = "Terms",
                Parent = "",
                Path = "terms",
                Order = seed + 13,
                Icon = Icons.List,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission>
                    {
                        new Permission(PermissionNames.View, RoleNames.Everyone, true),
                        new Permission(PermissionNames.View, RoleNames.Admin, true),
                        new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                    },
                PageTemplateModules = new List<PageTemplateModule>
                    {
                        new PageTemplateModule { ModuleDefinitionName = "Stomme.Modules.HtmlText, Stomme.Client", Title = "Terms of Use", Pane = PaneNames.Default,
                            PermissionList = new List<Permission> {
                                new Permission(PermissionNames.View, RoleNames.Everyone, true),
                                new Permission(PermissionNames.View, RoleNames.Admin, true),
                                new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                            },
                            Settings = new List<Setting> {
                                new Setting { SettingName = "DynamicTokens", SettingValue = "true" }
                            },
                            Content = _localizer["Terms"]
                        }
                    }
            });

            pageTemplates.Add(new PageTemplate
            {
                Name = "Not Found",
                Parent = "",
                Path = "404",
                Order = seed + 15,
                Icon = Icons.X,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.View, RoleNames.Everyone, true),
                    new Permission(PermissionNames.View, RoleNames.Admin, true),
                    new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                },
                PageTemplateModules = new List<PageTemplateModule>
                {
                    new PageTemplateModule { ModuleDefinitionName = "Stomme.Modules.HtmlText, Stomme.Client", Title = "Not Found", Pane = PaneNames.Default,
                        PermissionList = new List<Permission> {
                            new Permission(PermissionNames.View, RoleNames.Everyone, true),
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = "<p>The page you requested does not exist or you do not have sufficient rights to view it.</p>"
                    }
                }
            });

            // admin pages
            pageTemplates.Add(new PageTemplate
            {
                Name = "Admin",
                Parent = "",
                Path = "admin",
                Order = seed + 51,
                Icon = "",
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
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Dashboard.Index).ToModuleDefinitionName(), Title = "Admin Dashboard", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = ""
                    }
                }
            });
            pageTemplates.Add(new PageTemplate
            {
                Name = "Site Settings",
                Parent = "Admin",
                Order = 1,
                Path = "admin/site",
                Icon = Icons.Home,
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
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Site.Index).ToModuleDefinitionName(), Title = "Site Settings", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = ""
                    }
                }
            });
            pageTemplates.Add(new PageTemplate
            {
                Name = "Page Management",
                Parent = "Admin",
                Order = 3,
                Path = "admin/pages",
                Icon = Icons.Layers,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.View, RoleNames.Admin, true),
                    new Permission(PermissionNames.View, RoleNames.Registered, true), // required to support personalized pages
                    new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                },
                PageTemplateModules = new List<PageTemplateModule>
                {
                    new PageTemplateModule
                    {
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Pages.Index).ToModuleDefinitionName(), Title = "Page Management", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.View, RoleNames.Registered, true), // required to support personalized pages
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = ""
                    }
                }
            });
            pageTemplates.Add(new PageTemplate
            {
                Name = "User Management",
                Parent = "Admin",
                Order = 5,
                Path = "admin/users",
                Icon = Icons.People,
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
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Users.Index).ToModuleDefinitionName(), Title = "User Management", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = ""
                    }
                }
            });
            pageTemplates.Add(new PageTemplate
            {
                Name = "Profile Management",
                Parent = "Admin",
                Order = 7,
                Path = "admin/profiles",
                Icon = Icons.Person,
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
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Profiles.Index).ToModuleDefinitionName(), Title = "Profile Management", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = ""
                    }
                }
            });
            pageTemplates.Add(new PageTemplate
            {
                Name = "Role Management",
                Parent = "Admin",
                Order = 9,
                Path = "admin/roles",
                Icon = Icons.LockLocked,
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
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Roles.Index).ToModuleDefinitionName(), Title = "Role Management", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = ""
                    }
                }
            });
            pageTemplates.Add(new PageTemplate
            {
                Name = "File Management",
                Parent = "Admin",
                Order = 11,
                Path = "admin/files",
                Icon = Icons.File,
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
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Files.Index).ToModuleDefinitionName(), Title = "File Management", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = ""
                    }
                }
            });
            pageTemplates.Add(new PageTemplate
            {
                Name = "Recycle Bin",
                Parent = "Admin",
                Order = 13,
                Path = "admin/recyclebin",
                Icon = Icons.Trash,
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
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.RecycleBin.Index).ToModuleDefinitionName(), Title = "Recycle Bin", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = ""
                    }
                }
            });
            pageTemplates.Add(new PageTemplate
            {
                Name = "Url Mappings",
                Parent = "Admin",
                Order = 15,
                Path = "admin/urlmappings",
                Icon = Icons.LinkBroken,
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
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.UrlMappings.Index).ToModuleDefinitionName(), Title = "Url Mappings", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = ""
                    }
                }
            });

            pageTemplates.Add(new PageTemplate
            {
                Name = "Visitor Management",
                Parent = "Admin",
                Order = 17,
                Path = "admin/visitors",
                Icon = Icons.Eye,
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
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Visitors.Index).ToModuleDefinitionName(), Title = "Visitor Management", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = ""
                    }
                }
            });

            pageTemplates.Add(new PageTemplate
            {
                Name = "Search Settings",
                Parent = "Admin",
                Order = 19,
                Path = "admin/search",
                Icon = Icons.MagnifyingGlass,
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
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Search.Index).ToModuleDefinitionName(), Title = "Search Settings", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = ""
                    }
                }
            });
            pageTemplates.Add(new PageTemplate
            {
                Name = "Language Management",
                Parent = "Admin",
                Order = 21,
                Path = "admin/languages",
                Icon = Icons.Text,
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
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Languages.Index).ToModuleDefinitionName(), Title = "Language Management", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = ""
                    }
                }
            });
            pageTemplates.Add(new PageTemplate
            {
                Name = "Global Replace",
                Parent = "Admin",
                Order = 23,
                Path = "admin/replace",
                Icon = Icons.LoopSquare,
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
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.GlobalReplace.Index).ToModuleDefinitionName(), Title = "Global Replace", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = ""
                    }
                }
            });

            // host pages (order starts at 51)
            pageTemplates.Add(new PageTemplate
            {
                Name = "Event Log",
                Parent = "Admin",
                Order = 51,
                Path = "admin/log",
                Icon = Icons.List,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.View, RoleNames.Host, true),
                    new Permission(PermissionNames.Edit, RoleNames.Host, true)
                },
                PageTemplateModules = new List<PageTemplateModule>
                {
                    new PageTemplateModule
                    {
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Logs.Index).ToModuleDefinitionName(), Title = "Event Log", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Host, true),
                            new Permission(PermissionNames.Edit, RoleNames.Host, true)
                        },
                        Content = ""
                    }
                }
            });
            pageTemplates.Add(new PageTemplate
            {
                Name = "Site Management",
                Parent = "Admin",
                Order = 53,
                Path = "admin/sites",
                Icon = Icons.Globe,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.View, RoleNames.Host, true),
                    new Permission(PermissionNames.Edit, RoleNames.Host, true)
                },
                PageTemplateModules = new List<PageTemplateModule>
                {
                    new PageTemplateModule
                    {
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Sites.Index).ToModuleDefinitionName(), Title = "Site Management", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Host, true),
                            new Permission(PermissionNames.Edit, RoleNames.Host, true)
                        },
                        Content = ""
                    }
                }
            });
            pageTemplates.Add(new PageTemplate
            {
                Name = "Module Management",
                Parent = "Admin",
                Order = 55,
                Path = "admin/modules",
                Icon = Icons.Browser,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.View, RoleNames.Host, true),
                    new Permission(PermissionNames.Edit, RoleNames.Host, true)
                },
                PageTemplateModules = new List<PageTemplateModule>
                {
                    new PageTemplateModule
                    {
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.ModuleDefinitions.Index).ToModuleDefinitionName(), Title = "Module Management", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Host, true),
                            new Permission(PermissionNames.Edit, RoleNames.Host, true)
                        },
                        Content = ""
                    }
                }
            });
            pageTemplates.Add(new PageTemplate
            {
                Name = "Theme Management",
                Parent = "Admin",
                Order = 57,
                Path = "admin/themes",
                Icon = Icons.Brush,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.View, RoleNames.Host, true),
                    new Permission(PermissionNames.Edit, RoleNames.Host, true)
                },
                PageTemplateModules = new List<PageTemplateModule>
                {
                    new PageTemplateModule
                    {
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Themes.Index).ToModuleDefinitionName(), Title = "Theme Management", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Host, true),
                            new Permission(PermissionNames.Edit, RoleNames.Host, true)
                        },
                        Content = ""
                    }
                }
            });
            pageTemplates.Add(new PageTemplate
            {
                Name = "Scheduled Jobs",
                Parent = "Admin",
                Order = 59,
                Path = "admin/jobs",
                Icon = Icons.Timer,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.View, RoleNames.Host, true),
                    new Permission(PermissionNames.Edit, RoleNames.Host, true)
                },
                PageTemplateModules = new List<PageTemplateModule>
                {
                    new PageTemplateModule
                    {
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Jobs.Index).ToModuleDefinitionName(), Title = "Scheduled Jobs", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Host, true),
                            new Permission(PermissionNames.Edit, RoleNames.Host, true)
                        },
                        Content = ""
                    }
                }
            });
            pageTemplates.Add(new PageTemplate
            {
                Name = "Sql Management",
                Parent = "Admin",
                Order = 61,
                Path = "admin/sql",
                Icon = Icons.Spreadsheet,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.View, RoleNames.Host, true),
                    new Permission(PermissionNames.Edit, RoleNames.Host, true)
                },
                PageTemplateModules = new List<PageTemplateModule>
                {
                    new PageTemplateModule
                    {
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Sql.Index).ToModuleDefinitionName(), Title = "Sql Management", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Host, true),
                            new Permission(PermissionNames.Edit, RoleNames.Host, true)
                        },
                        Content = ""
                    }
                }
            });
            pageTemplates.Add(new PageTemplate
            {
                Name = "System Info",
                Parent = "Admin",
                Order = 63,
                Path = "admin/system",
                Icon = Icons.MedicalCross,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.View, RoleNames.Host, true),
                    new Permission(PermissionNames.Edit, RoleNames.Host, true)
                },
                PageTemplateModules = new List<PageTemplateModule>
                {
                    new PageTemplateModule
                    {
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.SystemInfo.Index).ToModuleDefinitionName(), Title = "System Info", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Host, true),
                            new Permission(PermissionNames.Edit, RoleNames.Host, true)
                        },
                        Content = ""
                    }
                }
            });
            pageTemplates.Add(new PageTemplate
            {
                Name = "System Update",
                Parent = "Admin",
                Order = 65,
                Path = "admin/update",
                Icon = Icons.Aperture,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.View, RoleNames.Host, true),
                    new Permission(PermissionNames.Edit, RoleNames.Host, true)
                },
                PageTemplateModules = new List<PageTemplateModule>
                {
                    new PageTemplateModule
                    {
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Upgrade.Index).ToModuleDefinitionName(), Title = "System Update", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Host, true),
                            new Permission(PermissionNames.Edit, RoleNames.Host, true)
                        },
                        Content = ""
                    }
                }
            });
            pageTemplates.Add(new PageTemplate
            {
                Name = "Setting Management",
                Parent = "Admin",
                Order = 67,
                Path = "admin/settings",
                Icon = Icons.Cog,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.View, RoleNames.Host, true),
                    new Permission(PermissionNames.Edit, RoleNames.Host, true)
                },
                PageTemplateModules = new List<PageTemplateModule>
                {
                    new PageTemplateModule
                    {
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Settings.Index).ToModuleDefinitionName(), Title = "Setting Management", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Host, true),
                            new Permission(PermissionNames.Edit, RoleNames.Host, true)
                        },
                        Content = ""
                    }
                }
            });

            // The feature-flag admin surface. Reads are Admin (matching FeatureController.Get);
            // writes are Host-gated server-side and the UI hides them below Host. Seeded unconditionally —
            // the flag subsystem always ships — and reaches existing installs through the boot-path
            // admin-page reconciliation (ProcessAdminPageTemplatesAsync).
            pageTemplates.Add(new PageTemplate
            {
                Name = "Feature Flags",
                Parent = "Admin",
                Order = 68,
                Path = "admin/features",
                Icon = Icons.Flag,
                IsNavigation = false,
                IsPersonalizable = false,
                PermissionList = new List<Permission>
                {
                    new Permission(PermissionNames.View, RoleNames.Admin, true),
                    new Permission(PermissionNames.Edit, RoleNames.Host, true)
                },
                PageTemplateModules = new List<PageTemplateModule>
                {
                    new PageTemplateModule
                    {
                        ModuleDefinitionName = typeof(Stomme.Modules.Admin.Features.Index).ToModuleDefinitionName(), Title = "Feature Flags", Pane = PaneNames.Default,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Host, true)
                        },
                        Content = ""
                    }
                }
            });

            // The dead-letter queue admin page — same opt-in posture as OpenIddict below: the bus
            // is opt-in (Messaging:Enabled), and a "Message Queue" page that can only ever say "disabled" is
            // noise in a stock admin menu. Reaches existing bus-enabled installs via boot reconciliation.
            if (_configuration.GetValue<bool>("Messaging:Enabled"))
            {
                pageTemplates.Add(new PageTemplate
                {
                    Name = "Message Queue",
                    Parent = "Admin",
                    Order = 70,
                    Path = "admin/deadletters",
                    Icon = Icons.Inbox,
                    IsNavigation = false,
                    IsPersonalizable = false,
                    PermissionList = new List<Permission>
                    {
                        new Permission(PermissionNames.View, RoleNames.Host, true),
                        new Permission(PermissionNames.Edit, RoleNames.Host, true)
                    },
                    PageTemplateModules = new List<PageTemplateModule>
                    {
                        new PageTemplateModule
                        {
                            ModuleDefinitionName = typeof(Stomme.Modules.Admin.DeadLetters.Index).ToModuleDefinitionName(), Title = "Message Queue", Pane = PaneNames.Default,
                            PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.View, RoleNames.Host, true),
                                new Permission(PermissionNames.Edit, RoleNames.Host, true)
                            },
                            Content = ""
                        }
                    }
                });
            }

            // The per-tenant OpenIddict authority is OPT-IN (OpenIddict:Enabled), and so is its admin page: a
            // stock install must be byte-for-byte unaffected, and an "Identity Provider" page that can only ever
            // say "disabled" is noise in the admin menu. Same gating posture as the bundled-module knob —
            // read config here, seed conditionally.
            if (_configuration.GetValue<bool>("OpenIddict:Enabled"))
            {
                pageTemplates.Add(new PageTemplate
                {
                    Name = "Identity Provider",
                    Parent = "Admin",
                    Order = 69,
                    Path = "admin/openiddict",
                    Icon = Icons.Shield,
                    IsNavigation = false,
                    IsPersonalizable = false,
                    PermissionList = new List<Permission>
                    {
                        new Permission(PermissionNames.View, RoleNames.Host, true),
                        new Permission(PermissionNames.Edit, RoleNames.Host, true)
                    },
                    PageTemplateModules = new List<PageTemplateModule>
                    {
                        new PageTemplateModule
                        {
                            ModuleDefinitionName = typeof(Stomme.Modules.Admin.OpenIddict.Index).ToModuleDefinitionName(), Title = "Identity Provider", Pane = PaneNames.Default,
                            PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.View, RoleNames.Host, true),
                                new Permission(PermissionNames.Edit, RoleNames.Host, true)
                            },
                            Content = ""
                        }
                    }
                });
            }

            // Data export is OPT-IN (DataExport:Enabled), and so is its admin page - the OpenIddict posture above: a stock
            // install is unaffected, and a page that can only say "disabled" is noise. Reaches an EXISTING site through the
            // additive boot-path reconciliation when the feature is switched on later. Administrators, because the export
            // keys default to Administrators; what the page shows is decided per key, and again by the server.
            if (_configuration.GetValue<bool>("DataExport:Enabled"))
            {
                pageTemplates.Add(new PageTemplate
                {
                    Name = "Data Export",
                    Parent = "Admin",
                    Order = 71,
                    Path = "admin/dataexport",
                    Icon = Icons.DataTransferDownload,
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
                            ModuleDefinitionName = typeof(Stomme.Modules.DataExport.Index).ToModuleDefinitionName(), Title = "Data Export", Pane = PaneNames.Default,
                            PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.View, RoleNames.Admin, true),
                                new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                            },
                            Content = ""
                        }
                    }
                });
            }

            // Portability is OPT-IN (Portability:Enabled), and so is its admin page - the Data Export posture above. Reaches
            // an EXISTING site through the additive boot-path reconciliation when switched on later. Administrators, because
            // the portability keys other than export-own default there; each action is decided per key by the server.
            if (_configuration.GetValue<bool>("Portability:Enabled"))
            {
                pageTemplates.Add(new PageTemplate
                {
                    Name = "Portability",
                    Parent = "Admin",
                    Order = 72,
                    Path = "admin/portability",
                    Icon = Icons.DataTransferUpload,
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
                            ModuleDefinitionName = typeof(Stomme.Modules.Portability.Index).ToModuleDefinitionName(), Title = "Portability", Pane = PaneNames.Default,
                            PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.View, RoleNames.Admin, true),
                                new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                            },
                            Content = ""
                        }
                    }
                });
            }
            // Pipelines is OPT-IN (Pipelines:Enabled), and so is its admin page. Administrators,
            // because every pipelines key defaults there; each action is decided per key by the server, on the request's site.
            if (_configuration.GetValue<bool>("Pipelines:Enabled"))
            {
                pageTemplates.Add(new PageTemplate
                {
                    Name = "Pipelines",
                    Parent = "Admin",
                    Order = 73,
                    Path = "admin/pipelines",
                    Icon = Icons.Fork,
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
                            ModuleDefinitionName = typeof(Stomme.Modules.Pipelines.Index).ToModuleDefinitionName(), Title = "Pipelines", Pane = PaneNames.Default,
                            PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.View, RoleNames.Admin, true),
                                new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                            },
                            Content = ""
                        }
                    }
                });
            }
            // Backup is OPT-IN (Backup:Enabled), and so is its admin page. HOST ONLY - the page and the module,
            // like the API behind them: an archive holds every site's users and password hashes (refutation 8).
            if (_configuration.GetValue<bool>("Backup:Enabled"))
            {
                pageTemplates.Add(new PageTemplate
                {
                    Name = "Backup",
                    Parent = "Admin",
                    Order = 73,
                    Path = "admin/backup",
                    Icon = Icons.HardDrive,
                    IsNavigation = false,
                    IsPersonalizable = false,
                    PermissionList = new List<Permission>
                    {
                        new Permission(PermissionNames.View, RoleNames.Host, true),
                        new Permission(PermissionNames.Edit, RoleNames.Host, true)
                    },
                    PageTemplateModules = new List<PageTemplateModule>
                    {
                        new PageTemplateModule
                        {
                            ModuleDefinitionName = typeof(Stomme.Modules.Backup.Index).ToModuleDefinitionName(), Title = "Backup", Pane = PaneNames.Default,
                            PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.View, RoleNames.Host, true),
                                new Permission(PermissionNames.Edit, RoleNames.Host, true)
                            },
                            Content = ""
                        }
                    }
                });
            }
            // MCP is OPT-IN on BOTH of its switches, and so is its admin page - the Data Export posture above. The page
            // follows Mcp:Enabled (the CLIENT half: the external-server registrations it opens on) OR
            // Mcp:Server:Enabled (the endpoint this install publishes, whose catalog and audit ledger the other two
            // controls show), because either one alone makes the page worth reaching. A stock install has neither, gains
            // no page, and a page that could only say "disabled" is noise. Switching a half on later reaches an EXISTING
            // site through the additive boot-path reconciliation. Administrators, because all three MCP keys default
            // there; what each control shows is decided per key, and again by the server on every request.
            if (_configuration.GetValue<bool>(Stomme.Modules.Mcp.Startup.McpSettings.ClientEnabledKey)
                || _configuration.GetValue<bool>(Stomme.Modules.Mcp.Startup.McpSettings.ServerEnabledKey))
            {
                pageTemplates.Add(new PageTemplate
                {
                    Name = "MCP",
                    Parent = "Admin",
                    Order = 72,
                    Path = "admin/mcp",
                    Icon = Icons.LinkIntact,
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
                            ModuleDefinitionName = typeof(Stomme.Modules.Mcp.Index).ToModuleDefinitionName(), Title = "MCP", Pane = PaneNames.Default,
                            PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.View, RoleNames.Admin, true),
                                new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                            },
                            Content = ""
                        }
                    }
                });
            }

            // Contextual help is OPT-IN (Help:Enabled, default FALSE), and so is its admin page - the Data
            // Export posture above: a stock install gains no page, and switching help on later reaches an EXISTING site
            // through the additive boot-path reconciliation. Administrators, because help:author and help:manage default
            // there; what the control offers is decided per key, and again by the server on every request.
            if (_configuration.GetValue<bool>(Stomme.Modules.ContextualHelp.Startup.HelpSettings.EnabledKey))
            {
                pageTemplates.Add(new PageTemplate
                {
                    Name = "Help",
                    Parent = "Admin",
                    Order = 73,
                    Path = "admin/help",
                    Icon = Icons.Book,
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
                            ModuleDefinitionName = typeof(Stomme.Modules.ContextualHelp.Index).ToModuleDefinitionName(), Title = "Help", Pane = PaneNames.Default,
                            PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.View, RoleNames.Admin, true),
                                new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                            },
                            Content = ""
                        }
                    }
                });
            }

            // The Host-only licensing page exists only under Deployment:Mode=OnPrem, like the licence ring itself - a
            // Hosted install composes nothing licensing-related. Reaches an existing site switched to OnPrem through the
            // boot-path reconciliation; switching back never removes it (reconciliation is additive), and the page and
            // api/Licence then answer "licensing is not enabled".
            if (Stomme.Infrastructure.Licensing.DeploymentMode.IsOnPrem(_configuration))
            {
                pageTemplates.Add(new PageTemplate
                {
                    Name = "Licensing",
                    Parent = "Admin",
                    Order = 71,
                    Path = "admin/licensing",
                    Icon = Icons.Key,
                    IsNavigation = false,
                    IsPersonalizable = false,
                    PermissionList = new List<Permission>
                    {
                        new Permission(PermissionNames.View, RoleNames.Host, true),
                        new Permission(PermissionNames.Edit, RoleNames.Host, true)
                    },
                    PageTemplateModules = new List<PageTemplateModule>
                    {
                        new PageTemplateModule
                        {
                            ModuleDefinitionName = typeof(Stomme.Modules.Admin.Licensing.Index).ToModuleDefinitionName(), Title = "Licensing", Pane = PaneNames.Default,
                            PermissionList = new List<Permission>
                            {
                                new Permission(PermissionNames.View, RoleNames.Host, true),
                                new Permission(PermissionNames.Edit, RoleNames.Host, true)
                            },
                            Content = ""
                        }
                    }
                });
            }
            return Task.FromResult(pageTemplates);
        }
    }
}
