using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Bunit;
using Stomme.Models;
using Stomme.Security;
using Stomme.Services;
using Stomme.Shared;
using Stomme.Tests.Help;
using Stomme.UI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;
using Dashboard = Stomme.Modules.Admin.Dashboard.Index;
using ModuleModel = Stomme.Models.Module;

namespace Stomme.Tests
{
    /// <summary>
    /// The Admin Dashboard has a search: a box above the tiles, and each tile carrying what the search matches.
    /// </summary>
    /// <remarks>
    /// Around forty tiles on a site with the modules on, nothing to filter them by. The
    /// dashboard control is STATIC, so the filter is a page script (<c>dashboard.js</c>) over the tiles' <c>data-search</c>,
    /// which runs on static and interactive pages alike. Its keyboard behaviour - typing filters, Enter opens the only
    /// match, Escape clears - was driven in a browser on a Server and a WebAssembly site (Client README).
    /// </remarks>
    public sealed class AdminDashboardSearchTests : BunitContext
    {
        public AdminDashboardSearchTests()
        {
            JSInterop.Mode = JSRuntimeMode.Loose;
            Services.AddSingleton(new SiteState { Alias = Alias() });
            Services.AddSingleton(HelpControlClickTests.Inert<ILogService>.Create());
            Services.AddSingleton(HelpControlClickTests.Inert<IPageService>.Create());
            Services.AddSingleton(HelpControlClickTests.Inert<IUserService>.Create());
            Services.AddSingleton<IStringLocalizer<SharedResources>>(new KeyLocalizer());
        }

        [Fact]
        public void The_dashboard_has_a_search_box_and_every_tile_carries_what_it_matches()
        {
            var cut = Render<Dashboard>(parameters => parameters
                .AddCascadingValue(Page())
                .AddCascadingValue(new ModuleModel { ModuleId = 1, SiteId = 1, PermissionList = new List<Permission> { new(PermissionNames.View, RoleNames.Registered, true) } }));

            var input = cut.Find(".db-dash .db-dash-search input[type=search]");
            Assert.Equal("db-dash-grid", input.GetAttribute("aria-controls"));
            Assert.NotNull(cut.Find("#db-dash-grid"));

            var tiles = cut.FindAll(".db-dash-tile");
            Assert.Equal(new[] { "Users", "Pipelines" }, tiles.Select(tile => tile.QuerySelector(".db-dash-tile-label")!.TextContent));
            Assert.Equal("users admin/users", tiles[0].GetAttribute("data-search"));
            Assert.Equal("pipelines run a pipeline admin/pipelines", tiles[1].GetAttribute("data-search"));

            // Nothing is filtered until the script runs, so the "nothing matches" line starts hidden.
            Assert.True(cut.Find(".db-dash-empty").HasAttribute("hidden"));
        }

        [Fact]
        public void A_tile_the_user_may_not_view_is_not_rendered_for_the_search_to_find()
        {
            var page = Page();
            page.Pages.Single(p => p.Path == "admin/pipelines").PermissionList = new List<Permission> { new(PermissionNames.View, RoleNames.Host, true) };

            var cut = Render<Dashboard>(parameters => parameters
                .AddCascadingValue(page)
                .AddCascadingValue(new ModuleModel { ModuleId = 1, SiteId = 1, PermissionList = new List<Permission> { new(PermissionNames.View, RoleNames.Registered, true) } }));

            Assert.Single(cut.FindAll(".db-dash-tile"));
            Assert.DoesNotContain("pipelines", cut.Markup);
        }

        [Fact]
        public void The_dashboard_declares_its_search_script_as_a_page_script_that_exists()
        {
            var script = Assert.Single(new Dashboard().Resources);
            Assert.Equal(ResourceType.Script, script.ResourceType);
            Assert.Equal(ResourceLoadBehavior.BlazorPageScript, script.LoadBehavior);
            Assert.EndsWith("/Modules/Stomme.Modules.Admin.Dashboard/dashboard.js", script.Url);

            var file = Path.Combine(RepositoryRoot(), "src", "Stomme.Server", "wwwroot", "Modules", "Stomme.Modules.Admin.Dashboard", "dashboard.js");
            var source = System.IO.File.ReadAllText(file);
            Assert.Contains("export function onLoad()", source);
            Assert.Contains("export function onUpdate()", source);
            Assert.Contains("data-search", source);
        }

        private static Alias Alias() => new() { AliasId = 1, SiteId = 1, TenantId = 1, Name = "localhost" };

        private static PageState Page()
        {
            var viewable = new List<Permission> { new(PermissionNames.View, RoleNames.Admin, true) };
            return new PageState
            {
                Alias = Alias(),
                Site = new Site
                {
                    SiteId = 1,
                    Name = "Site",
                    Pages = new List<Page>
                    {
                        new() { PageId = 1, SiteId = 1, Path = "admin", Name = "Admin", PermissionList = viewable },
                        new() { PageId = 2, SiteId = 1, ParentId = 1, Path = "admin/users", Name = "Users", PermissionList = viewable },
                        new() { PageId = 3, SiteId = 1, ParentId = 1, Path = "admin/pipelines", Name = "Pipelines", Title = "Run a pipeline", PermissionList = viewable },
                        new() { PageId = 4, SiteId = 1, Path = "home", Name = "Home", PermissionList = viewable },
                    },
                },
                Page = new Page { PageId = 1, SiteId = 1, Path = "admin", Name = "Admin" },
                User = new User { UserId = 7, Username = "user7", Roles = ";" + RoleNames.Registered + ";" + RoleNames.Admin + ";" },
                RenderMode = RenderModes.Static,
                QueryString = new Dictionary<string, string>(),
                UrlParameters = "",
            };
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !System.IO.File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found above the test output.");
        }

        private sealed class KeyLocalizer : IStringLocalizer<SharedResources>
        {
            public LocalizedString this[string name] => new(name, name);
            public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(name, arguments));
            public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => Array.Empty<LocalizedString>();
        }
    }
}
