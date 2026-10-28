#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Stomme.Dashboards;
using Stomme.Landing;
using Stomme.Models;
using Stomme.Modules.Dashboards.Infrastructure;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Landing
{
    /// <summary>
    /// BL-98 increment 1 - the Landing module on an installed host, over HTTP, on SQLite, PostgreSQL and RavenDB: every site's
    /// home page carries the module, untitled; a visitor reads nothing until the host makes AND publishes the page; a registered
    /// user cannot write; a link that is not a site path or an http(s) address is refused; a reset empties it again. And
    /// <c>module.landingpage.enabled = false</c> keeps it off the home page (D85).
    /// </summary>
    /// <remarks>
    /// The home page is public, so its view by a visitor must open NO circuit - with the page unmade AND published. The first
    /// version put the editor on the home page as an interactive island; the Help suite's anonymous-view oracle (Blazor marks
    /// every interactive component it renders with <c>&lt;!--Blazor:</c>) caught it, and the editor moved to the module's
    /// interactive Edit action. The oracle is validated the other way round here too: that action carries a marker.
    /// Increment 2: a published dashboard as the landing page - the Dashboards module's read-only board, rendered statically,
    /// its charts carrying what the page script draws - shown only to a visitor who may read dashboards.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public sealed class LandingDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Api = "/api/Landing";
        private const string LandingDefinition = "Stomme.Modules.Landing, Stomme.Client";
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private static readonly Regex InteractiveMarker = new("<!--Blazor:", RegexOptions.CultureInvariant);

        public static TheoryData<string> Stores() => new() { SqliteGateStore.StoreName, PostgresGateStore.StoreName, RavenGateStore.StoreName };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task The_home_page_carries_the_landing_page_which_a_visitor_sees_once_published(string storeName)
        {
            using var scratch = await GateInstallTemplates.CloneAsync(LiftedRequiresGateTests.ResolveStore(storeName), Host, output, colocateModuleAggregates: storeName == PostgresGateStore.StoreName, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                // The module is on the home page, untitled - it adds nothing a visitor can see until a page is made.
                var placement = await HomePlacementAsync(app);
                Assert.Equal("-", placement?.Title);
                var editAction = $"/*/{placement!.Value.ModuleId}/Edit";

                using var visitor = GateDrive.NewClient(app, Host);
                Assert.Equal(HttpStatusCode.NoContent, (await GateDrive.GetAsync(visitor, null, Host, Api, output, "a visitor, no page")).Status);
                var unmadeHome = await GateDrive.GetAsync(visitor, null, Host, "/", output, "a visitor's home page, no landing page");
                Assert.Equal(HttpStatusCode.OK, unmadeHome.Status);
                Assert.DoesNotContain("id=\"landing-", unmadeHome.Body, StringComparison.Ordinal);
                Assert.Empty(InteractiveMarker.Matches(unmadeHome.Body));

                using var hostClient = GateDrive.NewClient(app, Host);
                var hostCookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                // The host's home page links to the edit action, and the Landing component there is static: the edit action is
                // the module's one island, so it carries exactly one marker more than the home page's view (the control panel's).
                // A Static site does not prerender an interactive module (BL-57's default), so the chooser itself exists only
                // once the circuit connects - the click-through is the owner's.
                var hostHome = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/", output, "the host's home page, no landing page");
                Assert.Contains("id=\"landing-manage\"", hostHome.Body, StringComparison.Ordinal);
                Assert.Contains(editAction, hostHome.Body, StringComparison.Ordinal);
                var edit = await GateDrive.GetAsync(hostClient, hostCookies, Host, editAction, output, "the host's edit action");
                Assert.Equal(HttpStatusCode.OK, edit.Status);
                Assert.Equal(InteractiveMarker.Matches(hostHome.Body).Count + 1, InteractiveMarker.Matches(edit.Body).Count);

                // A link a public page must not carry is refused with its reason.
                var unsafeLink = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Put, $"{Api}/predefined",
                    new ConfigurePredefinedLandingRequest { Headline = "Welcome", Sections = { new LandingSectionView("Bad", "Link.", "Click", "javascript:alert(1)") } }, output, "an unsafe link");
                Assert.Equal(HttpStatusCode.BadRequest, unsafeLink.Status);
                Assert.Contains("not a link a landing page can carry", unsafeLink.Body);

                // Made, it is a draft: the host sees it, a visitor does not.
                var made = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Put, $"{Api}/predefined",
                    new ConfigurePredefinedLandingRequest { Headline = "Welcome to Acme", Introduction = "We make things.", Sections = { new LandingSectionView("Start", "Read this first.", "Go", "/start") } }, output, "make the page");
                Assert.Equal(HttpStatusCode.OK, made.Status);
                Assert.Equal(LandingStatus.Draft, JsonSerializer.Deserialize<LandingPageView>(made.Body, Json)!.Status);
                Assert.Equal(HttpStatusCode.NoContent, (await GateDrive.GetAsync(visitor, null, Host, Api, output, "a visitor, a draft")).Status);
                Assert.Contains("Welcome to Acme", (await GateDrive.GetAsync(hostClient, hostCookies, Host, Api, output, "the host, a draft")).Body);

                // A registered user holds landing:view only.
                using var plainClient = GateDrive.NewClient(app, Host);
                var plain = await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.SendJsonAsync(app, plainClient, plainCookies, Host, plain.UserId, HttpMethod.Post, $"{Api}/publish", new { }, output, "a registered user publishes")).Status);

                // Published, every visitor reads it.
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Post, $"{Api}/publish", new { }, output, "publish")).Status);
                var seen = await GateDrive.GetAsync(visitor, null, Host, Api, output, "a visitor, published");
                Assert.Equal(HttpStatusCode.OK, seen.Status);
                var page = JsonSerializer.Deserialize<LandingPageView>(seen.Body, Json)!;
                Assert.Equal((LandingSource.Predefined, LandingStatus.Published, "Welcome to Acme", "/start"), (page.Source, page.Status, page.Predefined!.Headline, page.Predefined.Sections[0].LinkUrl));

                // ... on the home page itself, rendered statically: the content, no manage link, no circuit.
                var publishedHome = await GateDrive.GetAsync(visitor, null, Host, "/", output, "a visitor's home page, published");
                Assert.Contains("id=\"landing-content\"", publishedHome.Body, StringComparison.Ordinal);
                Assert.Contains("Welcome to Acme", publishedHome.Body, StringComparison.Ordinal);
                Assert.Contains("href=\"/start\"", publishedHome.Body, StringComparison.Ordinal);
                Assert.DoesNotContain("id=\"landing-manage\"", publishedHome.Body, StringComparison.Ordinal);
                Assert.Empty(InteractiveMarker.Matches(publishedHome.Body));
                // ...at the TOP of the home page, above the site template's welcome text (the owner's walk found it below,
                // 2026-09-29: both were placed at order 1 and the tie kept the older module first).
                var welcome = publishedHome.Body.IndexOf("Welcome To Stomme", StringComparison.Ordinal);
                Assert.True(welcome > 0, "the site template's welcome text is on the home page");
                Assert.True(publishedHome.Body.IndexOf("id=\"landing-content\"", StringComparison.Ordinal) < welcome, "the landing page is above the welcome text");

                // Reset, it is gone - for the visitor, and publishing nothing is refused.
                Assert.Equal(HttpStatusCode.NoContent, (await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Delete, Api, null, output, "reset")).Status);
                Assert.Equal(HttpStatusCode.NoContent, (await GateDrive.GetAsync(visitor, null, Host, Api, output, "a visitor, after the reset")).Status);
                Assert.Equal(HttpStatusCode.Conflict, (await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Post, $"{Api}/publish", new { }, output, "publish nothing")).Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task The_module_knob_keeps_the_landing_page_off_the_home_page()
        {
            var off = new JsonObject { ["FeatureManagement"] = new JsonObject { ["module.landingpage.enabled"] = false } };
            // A fresh install, never a clone: this is about what the install SEEDS (GateInstallTemplates' own rule).
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host, off);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                Assert.Null(await HomePlacementAsync(app));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task A_published_dashboard_is_the_landing_page_rendered_statically_for_a_visitor_who_may_read_dashboards(string storeName)
        {
            using var scratch = await GateInstallTemplates.CloneAsync(LiftedRequiresGateTests.ResolveStore(storeName), Host, output,
                colocateModuleAggregates: storeName == PostgresGateStore.StoreName,
                extraSettings: new JsonObject { ["DemoContent"] = new JsonObject { ["Enabled"] = true } });
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var hostClient = GateDrive.NewClient(app, Host);
                var hostCookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var boards = JsonSerializer.Deserialize<List<DashboardView>>((await GateDrive.GetAsync(hostClient, hostCookies, Host, "/api/Dashboards", output, "the site's dashboards")).Body, Json)!;
                var board = boards.Single(candidate => candidate.Name == DashboardsDemoContentReconciler.BoardName);
                var chart = board.Widgets.Single(widget => widget.Kind == WidgetKind.Chart);
                var draft = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Post, "/api/Dashboards", new CreateDashboardRequest { Name = "Unfinished", Columns = 12, RowHeightPx = 40 }, output, "a draft board");
                var draftId = JsonSerializer.Deserialize<DashboardView>(draft.Body, Json)!.Id;

                // Only a published dashboard of the site.
                Assert.Equal(HttpStatusCode.NotFound, (await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Put, $"{Api}/dashboard", new ConfigureDashboardLandingRequest { DashboardId = Guid.NewGuid() }, output, "no such dashboard")).Status);
                Assert.Equal(HttpStatusCode.Conflict, (await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Put, $"{Api}/dashboard", new ConfigureDashboardLandingRequest { DashboardId = draftId }, output, "a draft dashboard")).Status);

                var made = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Put, $"{Api}/dashboard", new ConfigureDashboardLandingRequest { DashboardId = board.Id }, output, "the demo board");
                Assert.Equal(HttpStatusCode.OK, made.Status);
                Assert.Equal((LandingSource.Dashboard, (Guid?)board.Id), JsonSerializer.Deserialize<LandingPageView>(made.Body, Json) is { } view ? (view.Source, view.DashboardId) : default);
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostId, HttpMethod.Post, $"{Api}/publish", new { }, output, "publish")).Status);

                // dashboards:view is the administrators' by default: a visitor gets nothing - no board, no circuit.
                using var visitor = GateDrive.NewClient(app, Host);
                Assert.Equal(HttpStatusCode.NoContent, (await GateDrive.GetAsync(visitor, null, Host, Api, output, "a visitor who may not read dashboards")).Status);
                var hidden = await GateDrive.GetAsync(visitor, null, Host, "/", output, "that visitor's home page");
                Assert.DoesNotContain("id=\"landing-dashboard\"", hidden.Body, StringComparison.Ordinal);
                Assert.Empty(InteractiveMarker.Matches(hidden.Body));

                // Granted to every visitor, the board is on the home page - rendered statically, its chart carrying what the
                // page script draws, the page script and the board's styles on the page.
                await GrantAsync(app, DashboardPermissions.Resource, DashboardPermissions.View, RoleNames.Everyone);
                var home = await GateDrive.GetAsync(visitor, null, Host, "/", output, "a visitor who may read dashboards");
                Assert.Equal(HttpStatusCode.OK, home.Status);
                Assert.Contains("id=\"landing-dashboard\"", home.Body, StringComparison.Ordinal);
                Assert.Contains($"data-dashboard-id=\"{board.Id}\"", home.Body, StringComparison.Ordinal);
                Assert.All(board.Widgets, widget => Assert.Contains($"id=\"widget-{widget.Id}\"", home.Body, StringComparison.Ordinal));
                Assert.Contains($"id=\"db-chart-{chart.Id:N}\"", home.Body, StringComparison.Ordinal);
                Assert.Contains("data-db-chart-spec=", home.Body, StringComparison.Ordinal);
                Assert.Contains("data-db-chart-rows=", home.Body, StringComparison.Ordinal);
                Assert.Contains("<page-script src=\"/Modules/Landing/landing.js?v=", home.Body, StringComparison.Ordinal);
                Assert.Contains("Modules/Dashboards/dashboards.css", home.Body, StringComparison.Ordinal);
                Assert.DoesNotContain(DashboardBoardUnavailable, home.Body, StringComparison.Ordinal);
                Assert.DoesNotContain("id=\"landing-manage\"", home.Body, StringComparison.Ordinal);
                Assert.Empty(InteractiveMarker.Matches(home.Body));
                // The scripts themselves are not fetched here: this host's content root is a scratch folder and it maps no
                // static web assets. The chart drawn from this markup is verified in a browser (the module's README).
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        // The static board's fixed reason for a widget it could not read.
        private const string DashboardBoardUnavailable = Stomme.Modules.Dashboards.Components.DashboardBoard.Unavailable;

        private static Task<bool> GrantAsync(Microsoft.AspNetCore.Builder.WebApplication app, string resource, string key, string role) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var operation = key.Substring(key.IndexOf(':') + 1);
                var permissions = sp.GetRequiredService<IPermissionRepository>();
                var submitted = (await permissions.GetPermissionsAsync(alias.SiteId, resource, operation)).Where(row => row.EntityId == PermissionKeys.KeyEntityId).ToList();
                submitted.Add(new Permission(alias.SiteId, resource, PermissionKeys.KeyEntityId, operation, role, null, true));
                await permissions.UpdatePermissionsAsync(alias.SiteId, resource, PermissionKeys.KeyEntityId, submitted);
                return true;
            });

        // The title and module id of the Landing module's placement on the site's home page, or null when it is not there.
        private static Task<(string Title, int ModuleId)?> HomePlacementAsync(Microsoft.AspNetCore.Builder.WebApplication app) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var home = await sp.GetRequiredService<IPageRepository>().GetPageAsync(string.Empty, alias.SiteId) ?? throw new InvalidOperationException("The site has no home page.");
                return (await sp.GetRequiredService<IPageModuleRepository>().GetPageModulesAsync(alias.SiteId))
                    .Where(pageModule => pageModule.PageId == home.PageId && !pageModule.IsDeleted && pageModule.Module.ModuleDefinitionName == LandingDefinition)
                    .Select(pageModule => ((string Title, int ModuleId)?)(pageModule.Title, pageModule.ModuleId))
                    .FirstOrDefault();
            });
    }
}
