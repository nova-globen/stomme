using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Stomme.Documentation;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Infrastructure.SiteTemplates
{
    [PrivateApi("Mark Site-Template classes as private, since it's not very useful in the public docs")]
    public class DefaultSiteTemplate : ISiteTemplate
    {

        private readonly IWebHostEnvironment _environment;
        private readonly ISiteRepository _siteRepository;
        private readonly IFolderRepository _folderRepository;
        private readonly IFileRepository _fileRepository;

        public DefaultSiteTemplate(IWebHostEnvironment environment, ISiteRepository siteRepository, IFolderRepository folderRepository, IFileRepository fileRepository)
        {
            _environment = environment;
            _siteRepository = siteRepository;
            _folderRepository = folderRepository;
            _fileRepository = fileRepository;
        }

        public string Name
        {
            get { return "Default Site Template"; }
        }

        public async Task<List<PageTemplate>> CreateSiteAsync(Site site)
        {
            List<PageTemplate> _pageTemplates = new List<PageTemplate>();

            _pageTemplates.Add(new PageTemplate
            {
                Name = "Home",
                Parent = "",
                Order = 1,
                Path = "",
                Icon = "oi oi-home",
                IsNavigation = true,
                IsPersonalizable = false,
                PermissionList = new List<Permission> {
                    new Permission(PermissionNames.View, RoleNames.Everyone, true),
                    new Permission(PermissionNames.View, RoleNames.Admin, true),
                    new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                },
                PageTemplateModules = new List<PageTemplateModule> {
                    new PageTemplateModule { ModuleDefinitionName = "Stomme.Modules.HtmlText, Stomme.Client",
                        Title = "Welcome To Stomme...",
                        Pane = PaneNames.Default,
                        Order = 1,
                        PermissionList = new List<Permission> {
                            new Permission(PermissionNames.View, RoleNames.Everyone, true),
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = "<p style=\"text-align:center;margin:1rem 0;\"><a href=\"https://nova-globen.se/products/stomme\" target=\"_new\"><img src=\"stomme-glow.png\" alt=\"Nova Globen Stomme\" style=\"max-width:440px;width:100%;height:auto;\" /></a></p>" +
                        "<p style=\"font-size:1.05rem;\"><a href=\"https://nova-globen.se/products/stomme\" target=\"_new\">Nova Globen Stomme</a> is a <b>multi-tenant CMS</b> and <b>application framework</b> for <b>.NET&nbsp;10</b> and <b>Blazor</b>. It composes a fully dynamic digital experience out of pluggable modules and themes, runs on the render mode of your choice, and stores its data behind a provider-neutral seam that speaks both relational databases and a document store. Whether you want to accelerate your product work or study the anatomy of a large-scale Blazor application, Stomme gives you a foundation built on proven enterprise architecture.</p>" +
                        "<p class=\"db-btn-row\" style=\"justify-content:center;margin-top:1.25rem;\"><a class=\"db-btn\" href=\"https://nova-globen.se/products/stomme\" target=\"_new\">Join Our Community</a></p>"
                    },
                    new PageTemplateModule { ModuleDefinitionName = "Stomme.Modules.HtmlText, Stomme.Client",
                        Title = "Why Stomme",
                        Pane = PaneNames.Default,
                        Order = 2,
                        PermissionList = new List<Permission> {
                            new Permission(PermissionNames.View, RoleNames.Everyone, true),
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = "<p>Everything below is built in — no add-ons required.</p>" +
                        "<div class=\"db-grid\" style=\"margin-top:1rem;\">" +
                        "<div class=\"db-card\"><h3 style=\"margin:0;\">Multi-Tenancy</h3><p style=\"margin:0;color:var(--db-text-2);\">One installation hosts many sites and tenants. Requests resolve by hostname through an Alias &rarr; Site &rarr; Tenant model, so tenants can share a database or stay fully isolated.</p></div>" +
                        "<div class=\"db-card\"><h3 style=\"margin:0;\">Polyglot Persistence</h3><p style=\"margin:0;color:var(--db-text-2);\">A provider-neutral data seam runs on EF&nbsp;Core (SQL&nbsp;Server, PostgreSQL, SQLite) <b>and</b> RavenDB. Pick your store at install time; modules work identically on either.</p></div>" +
                        "<div class=\"db-card\"><h3 style=\"margin:0;\">Dynamic Modules &amp; Themes</h3><p style=\"margin:0;color:var(--db-text-2);\">Extensions plug in by convention, not by editing the framework. Drop in a module or theme assembly and it is discovered, registered and rendered automatically.</p></div>" +
                        "<div class=\"db-card\"><h3 style=\"margin:0;\">Built-in Identity</h3><p style=\"margin:0;color:var(--db-text-2);\">ASP.NET&nbsp;Core Identity with an integrated OpenIddict identity provider secures your content, and role-based permissions gate every page and module.</p></div>" +
                        "</div>"
                    },
                    new PageTemplateModule { ModuleDefinitionName = "Stomme.Modules.HtmlText, Stomme.Client",
                        Title = "A Modern .NET Foundation",
                        Pane = PaneNames.Default,
                        Order = 3,
                        PermissionList = new List<Permission> {
                            new Permission(PermissionNames.View, RoleNames.Everyone, true),
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = "<p><a href=\"https://dotnet.microsoft.com/apps/aspnet/web-apps/blazor\" target=\"_new\">Blazor</a> is a modern front-end web framework based on HTML, CSS and C#. It offers a component-based architecture with server-side rendering and full client-side interactivity in a single solution &mdash; Stomme lets you render each site as Static&nbsp;SSR, Blazor&nbsp;Server, WebAssembly, or a Blazor&nbsp;Hybrid desktop and mobile app, and even mix modes on the same page.</p>" +
                        "<p style=\"margin-top:1rem;\">The stack under the hood:</p>" +
                        "<p class=\"db-btn-row\">" +
                        Chip(".NET 10") + Chip("Blazor") + Chip("ASP.NET Core") + Chip("EF Core") + Chip("RavenDB") + Chip("OpenIddict") + Chip("FusionCache") +
                        "</p>"
                    },
                    new PageTemplateModule { ModuleDefinitionName = "Stomme.Modules.HtmlText, Stomme.Client",
                        Title = "Get Started",
                        Pane = PaneNames.Default,
                        Order = 4,
                        PermissionList = new List<Permission> {
                            new Permission(PermissionNames.View, RoleNames.Everyone, true),
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = "<p>You are looking at a fresh install seeded from the default starter template. Sign in with the host account you created during setup, then use the <b>Admin</b> menu to add pages, drop modules into panes and switch themes &mdash; all without leaving the browser. To start an app of your own on the framework's packages:</p>" +
                        "<pre style=\"background:var(--db-bg-elev-2);border:1px solid var(--db-border);border-radius:var(--db-radius,10px);padding:1rem;overflow-x:auto;\"><code>dotnet new install Stomme.Application.Template\ndotnet new stomme-app -o MyCompany.MyProject</code></pre>" +
                        "<p class=\"db-btn-row\" style=\"margin-top:1rem;\"><a class=\"db-btn\" href=\"https://nova-globen.se/products/stomme\" target=\"_new\">Read the Docs</a></p>"
                    },
                    new PageTemplateModule { ModuleDefinitionName = "Stomme.Modules.HtmlText, Stomme.Client",
                        Title = "Secure Content",
                        Pane = PaneNames.Default,
                        Order = 5,
                        PermissionList = new List<Permission> {
                            new Permission(PermissionNames.View, RoleNames.Registered, true),
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = "<p>You are seeing this because you are signed in. Stomme gates content with security roles &mdash; this module is visible only to <b>registered users</b> of the site, while the rest of this page is public. Edit its permissions from the module's action menu to change who can see it.</p>"
                    },
                    new PageTemplateModule { ModuleDefinitionName = "Stomme.Modules.HtmlText, Stomme.Client",
                        Title = "License",
                        Pane = PaneNames.Default,
                        Order = 6,
                        PermissionList = new List<Permission> {
                            new Permission(PermissionNames.View, RoleNames.Everyone, true),
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = "<p style=\"color:var(--db-text-2);font-size:0.9rem;\">© 2026 Nova Globen AB.</p>" +
                        "<p style=\"color:var(--db-text-2);font-size:0.9rem;\">Nova Globen Stomme is source-available software of Nova Globen AB, free under the PolyForm Noncommercial 1.0.0 or PolyForm Small Business 1.0.0 licence; any other commercial use needs a commercial licence from Nova Globen AB. Required Notice: Copyright Nova Globen AB (https://nova-globen.se). Portions derived from third-party works remain under their own licences, reproduced in its third-party notices.</p>"
                    }
                }
            });
            _pageTemplates.Add(new PageTemplate
            {
                Name = "Private",
                Parent = "",
                Order = 3,
                Path = "private",
                Icon = "oi oi-lock-locked",
                IsNavigation = true,
                IsPersonalizable = false,
                PermissionList = new List<Permission> {
                    new Permission(PermissionNames.View, RoleNames.Registered, true),
                    new Permission(PermissionNames.View, RoleNames.Admin, true),
                    new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                },
                PageTemplateModules = new List<PageTemplateModule> {
                    new PageTemplateModule { ModuleDefinitionName = "Stomme.Modules.HtmlText, Stomme.Client",
                        Title = "Secure Content",
                        Pane = PaneNames.Default,
                        Order = 1,
                        PermissionList = new List<Permission> {
                            new Permission(PermissionNames.View, RoleNames.Registered, true),
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = "<p>This entire page is protected. It appears in the menu and renders only for <b>registered users</b> &mdash; sign out and it disappears. Permissions are enforced per page <i>and</i> per module, so you can mix public and private content freely across your site.</p>"
                    }
                }
            });
            _pageTemplates.Add(new PageTemplate
            {
                Name = "My Page",
                Parent = "",
                Order = 5,
                Path = "mypage",
                Icon = "oi oi-target",
                IsNavigation = true,
                IsPersonalizable = true,
                PermissionList = new List<Permission> {
                    new Permission(PermissionNames.View, RoleNames.Everyone, true),
                    new Permission(PermissionNames.View, RoleNames.Admin, true),
                    new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                },
                PageTemplateModules = new List<PageTemplateModule> {
                    new PageTemplateModule { ModuleDefinitionName = "Stomme.Modules.HtmlText, Stomme.Client",
                        Title = "My Page",
                        Pane = PaneNames.Default,
                        Order = 1,
                        PermissionList = new List<Permission> {
                            new Permission(PermissionNames.View, RoleNames.Everyone, true),
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        },
                        Content = "<p>Stomme supports personalized pages out of the box. When a page is marked as <b>personalizable</b> in its settings, each authenticated visitor gets an edit button beside their username. Clicking it creates a private version of the page that only they can see and edit &mdash; perfect for dashboards, profiles and per-user landing pages.</p>"
                    }
                }
            });

            // The shipped brand asset is read through the web root's FILE PROVIDER, not a physical path. An app built on the
            // Stomme.Server package serves it under `dotnet run` as a static web asset from the package, with no file under
            // its web root, so its first site got no logo (BL-186 increment 5, measured 2026-10-01). A host with a physical
            // wwwroot, a published app included, is answered from that file as before.
            var logo = _environment.WebRootFileProvider?.GetFileInfo("images/logo-white.png");
            if (logo is { Exists: true, IsDirectory: false })
            {
                string folderpath = Utilities.PathCombine(_environment.ContentRootPath, "Content", "Tenants", site.TenantId.ToString(), "Sites", site.SiteId.ToString(), Path.DirectorySeparatorChar.ToString());
                System.IO.Directory.CreateDirectory(folderpath);
                // Always refresh the seeded logo from the shipped brand asset (the copy overwrites). A stale
                // logo-white.png left in the content folder by a PRIOR install (e.g. the pre-D63 oqtane mark)
                // must NOT survive a fresh site seed — the earlier "copy only if absent" guard let exactly
                // that residue render as a fresh install's header logo.
                await using (var source = logo.CreateReadStream())
                await using (var target = System.IO.File.Create(Path.Combine(folderpath, "logo-white.png")))
                {
                    await source.CopyToAsync(target);
                }
                Folder folder = await _folderRepository.GetFolderAsync(site.SiteId, "");
                Stomme.Models.File file = await _fileRepository.AddFileAsync(new Stomme.Models.File { FolderId = folder.FolderId, Name = "logo-white.png", Extension = "png", Size = (int)logo.Length, ImageHeight = 96, ImageWidth = 482 });
                site.LogoFileId = file.FileId;
                await _siteRepository.UpdateSiteAsync(site);
            }

            return _pageTemplates;
        }

        // A small pill for the tech-stack line. Uses --db-* tokens so it tracks the active theme (light/dark).
        private static string Chip(string label)
        {
            return "<span style=\"display:inline-block;padding:0.35rem 0.75rem;border:1px solid var(--db-border);border-radius:999px;background:var(--db-bg-elev-2);color:var(--db-text);font-size:13px;font-weight:600;\">" + label + "</span>";
        }
    }
}
