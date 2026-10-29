using System.Collections.Generic;
using Stomme.Models;
using Stomme.Modules;
using Stomme.Shared;

namespace NovaGloben.Module.Blogs
{
    /// <summary>
    /// The Blogs module's <see cref="ModuleDefinition"/> — how the framework discovers it. The
    /// <c>ModuleDefinitionName</c> is derived from this type's namespace + assembly
    /// (<c>NovaGloben.Module.Blogs, NovaGloben.Module.Blogs.Client.Stomme</c>), which is also how the Blazor
    /// host resolves the rendered component: <c>{namespace}.{action}</c> → <c>NovaGloben.Module.Blogs.Index</c>
    /// in the same assembly. Every assembly name contains "Stomme" so the convention loader loads them.
    /// <para>
    /// WASM-download-safe split: this CLIENT assembly carries the UI + the HTTP service; the SHARED
    /// assembly carries the wire model + contract; the SERVER assembly carries the controller, CQRS handlers
    /// and persistence wiring. On a WebAssembly site the framework streams the ModuleDefinition's
    /// <see cref="ModuleDefinition.AssemblyName"/> (this assembly) plus every assembly named in
    /// <see cref="ModuleDefinition.Dependencies"/> (a comma-separated list of assembly names, ".dll"
    /// optional — <c>ModuleDefinitionRepository.GetAssembliesAsync</c> strips it) to the browser — so
    /// Dependencies MUST name the Shared assembly and MUST NOT name the Server assembly.
    /// </para>
    /// <para>
    /// SELF-PLACEMENT (data only, no framework edit). This <see cref="ModuleDefinition.PageTemplates"/> makes
    /// the install seed a public "blogs" page carrying this module — the framework's
    /// <c>SiteRepository.ProcessPageTemplatesAsync</c> reads every discovered module definition's page
    /// templates and creates the pages, auto-filling <c>ModuleDefinitionName</c>/<c>Title</c>. This is the
    /// intended way an EXTERNALLY installed module lands on a page without editing the framework's
    /// <c>DefaultSiteTemplate</c> and without an <c>@page</c> route (which the CMS's own router does not use for
    /// module controls). View is granted to Everyone so a fresh, anonymous request renders the blog.
    /// </para>
    /// </summary>
    public class ModuleInfo : IModule
    {
        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = "Blogs",
            Description = "An independent, storage-blind Blogs module: publish and read blog posts. Showcases " +
                          "the four reliable-modularity properties — a NuGet-packages-only module (modularity), " +
                          "a provider-neutral aggregate the host persists on any store (flexible persistence), " +
                          "a mediator command/query pipeline (CQRS), and a DDD aggregate root + value object.",
            Categories = "Sample",
            Version = "2.0.0",
            // The Shared assembly streams to the WASM client beside this one; the Server assembly is
            // deliberately absent (it must never reach the browser).
            Dependencies = "NovaGloben.Module.Blogs.Shared.Stomme",
            PackageName = "NovaGloben.Module.Blogs",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "MIT",
            PageTemplates = new List<PageTemplate>
            {
                new PageTemplate
                {
                    Name = "Blog",
                    Parent = "",
                    Path = "blogs",
                    // AliasName "*" = all sites. This is REQUIRED, not cosmetic: SiteRepository.CreatePagesAsync
                    // skips any module page template whose AliasName is neither "*" nor the installing alias's
                    // name, and the PageTemplate default is "" — which never matches — so a module page seeded
                    // through ModuleDefinition.PageTemplates silently does not appear without it. (The framework's
                    // own @page-route-derived templates set "*" for exactly this reason.)
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
                            // ModuleDefinitionName + Title are auto-filled by ProcessPageTemplatesAsync from
                            // this module definition, so they are intentionally left at their defaults.
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
