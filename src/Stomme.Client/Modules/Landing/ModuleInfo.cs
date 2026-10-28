using System.Collections.Generic;
using Stomme.Landing;
using Stomme.Models;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Modules.Landing
{
    /// <summary>
    /// The Landing module (BL-98; the predecessor platform's feature 44, planned there and never built): a site's landing page, on its HOME page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Its page template names the home page's path</b>, and a template whose page already exists ADDS the module to that page
    /// (<c>SiteRepository.CreatePagesAsync</c>): every site gets the module on its <c>/</c> - a new one at install, an existing one
    /// on the module-template reconciliation - and <c>module.landingpage.enabled = false</c> keeps it off an installation (D85).
    /// The template's page settings apply only to a site that has no home page; an existing home keeps its own.
    /// </para>
    /// <para>
    /// <b>Untitled</b> (<c>"-"</c>, which every stock container honours) and <b>empty until made</b>: a visitor of a site without a
    /// published landing page sees nothing of the module; an administrator sees a link to its Edit action. The home page's
    /// component is STATIC, so no visitor's view opens a circuit; only the Edit action is interactive.
    /// </para>
    /// </remarks>
    public class ModuleInfo : IModule
    {
        public const string ModuleName = "Landing Page";

        public ModuleDefinition ModuleDefinition => new ModuleDefinition
        {
            Name = ModuleName,
            Description = "The site's landing page on its home page: made from a template, from one of the site's dashboards, or - once the AI assistant is here - generated; shown to visitors once published.",
            Categories = "Common",
            Version = "1.0.0",
            Owner = "Nova Globen",
            Url = "https://nova-globen.se/products/stomme",
            Contact = "https://github.com/nova-globen/stomme",
            License = "PolyForm-Noncommercial-1.0.0 OR PolyForm-Small-Business-1.0.0",
            PermissionNames = PermissionKeys.Declare(LandingPermissions.Declarations),
            PageTemplates = new List<PageTemplate>
            {
                new PageTemplate
                {
                    Name = "Home",
                    Parent = "",
                    Path = "",
                    AliasName = "*",
                    Version = "*",
                    Order = 1,
                    Icon = "oi oi-home",
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
                            Title = "-",
                            Pane = PaneNames.Default,
                            // BEFORE whatever a site template placed first (the default template's welcome text is at 1, and a
                            // tie kept the older module on top - found by the owner's walk, 2026-09-29). A reorder in the page
                            // editor renumbers 1, 3, 5..., so an operator can still move it; -1 only decides where it lands.
                            Order = -1,
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
