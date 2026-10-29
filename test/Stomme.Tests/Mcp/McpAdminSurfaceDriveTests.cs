#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Mcp;
using Stomme.Models.Mcp;
using Stomme.Modules.Mcp.Contributions;
using Stomme.Modules.Mcp.Domain;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Tests.Fixtures.McpTestModule;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Mcp
{
    /// <summary>
    /// The MCP admin surface: <c>api/Mcp</c> over real HTTP, and the admin PAGE that
    /// only exists when a half is switched on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two halves are gated at the ROUTER, and that is what this drives.</b> <c>[RequiresPermissionKey]</c> is
    /// an authorization POLICY and runs before any action body, so a config check inside an action would answer 403 to
    /// a caller without the key and never reach its own <c>NotFound</c> - measured, and the reason the two
    /// action constraints exist. What the wire must show is 404 for a half that is off, and 403 for a caller without
    /// the key on a half that is on - two different facts that a body-level check would collapse into one.
    /// </para>
    /// <para>
    /// SQLite, deliberately: none of this is a question about the provider - the per-site and per-store half is
    /// <c>McpSiteIsolationTests</c>, which runs on PostgreSQL and RavenDB. What is here is the HTTP surface and the
    /// page, and a second and third provider would pay two installs to assert the same status codes.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class McpAdminSurfaceDriveTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public McpAdminSurfaceDriveTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task The_admin_api_gates_each_half_at_the_router_and_each_action_on_its_own_key()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output,
                colocateModuleAggregates: false, extraSettings: McpCatalogDriveTests.McpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var plain = await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);
                var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                using var hostClient = GateDrive.NewClient(app, Host);
                using var plainClient = GateDrive.NewClient(app, Host);
                using var anonymousClient = GateDrive.NewClient(app, Host);
                var hostCookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, _output);

                // ---- the host account holds all three keys through the seeded Administrators rows -----------------
                var servers = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/api/Mcp/servers", _output, "host servers");
                Assert.Equal(HttpStatusCode.OK, servers.Status);
                Assert.Equal("[]", servers.Body.Trim());

                var published = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/api/Mcp/published", _output, "host published");
                Assert.Equal(HttpStatusCode.OK, published.Status);
                _output.WriteLine($"published -> {GateDrive.Truncate(published.Body)}");

                // The framework's own contribution and the fixture's, as the CALLER may use them - and the effective
                // approval, which is the catalog's to answer (a site override would show up here and nowhere else).
                var items = JsonNode.Parse(published.Body)!.AsArray();
                var names = items.Select(item => item?["name"]?.GetValue<string>()).ToList();
                Assert.Contains(StommeSiteContributor.SiteUri, names);
                Assert.Contains(McpTestContributor.Echo, names);
                Assert.DoesNotContain(McpTestContributor.Never, names);       // a Deny tool is not advertised
                Assert.Equal("Auto", items.First(item => item?["name"]?.GetValue<string>() == McpTestContributor.Echo)?["approval"]?.GetValue<string>());
                Assert.Equal("Confirm", items.First(item => item?["name"]?.GetValue<string>() == McpTestContributor.Confirm)?["approval"]?.GetValue<string>());

                var audit = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/api/Mcp/audit?days=7", _output, "host audit");
                Assert.Equal(HttpStatusCode.OK, audit.Status);

                // ---- a registered-only user holds NONE of them: 403, not an empty list ---------------------------
                // The difference matters: an empty list would say "there is nothing", and there is something.
                foreach (var path in new[] { "/api/Mcp/servers", "/api/Mcp/published", "/api/Mcp/audit?days=7" })
                {
                    var denied = await GateDrive.GetAsync(plainClient, plainCookies, Host, path, _output, "plain " + path);
                    Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
                }

                // ---- anonymous is refused too, and by the route -------------------------------------------------
                var anonymous = await GateDrive.GetAsync(anonymousClient, null, Host, "/api/Mcp/servers", _output, "anonymous servers");
                Assert.Equal(HttpStatusCode.Forbidden, anonymous.Status);

                // ---- the five by-id operations answer 404 for an id that exists nowhere --------------------------
                // The same answer another site's registration gets (McpSiteIsolationTests drives that half): the id
                // space must not tell an administrator what exists on the site next door.
                var nowhere = Guid.NewGuid();
                foreach (var (method, path) in ByIdRoutes(nowhere))
                {
                    var answer = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostUserId, method, path,
                        method == HttpMethod.Put ? new McpApiKeyRequest { ApiKey = "k" } : null, _output, "host " + path);
                    Assert.Equal(HttpStatusCode.NotFound, answer.Status);
                }

                // ...and a registered-only user gets 403 on the same routes - the KEY is decided before the id is.
                foreach (var (method, path) in ByIdRoutes(nowhere))
                {
                    var answer = await GateDrive.SendJsonAsync(app, plainClient, plainCookies, Host, plain.UserId, method, path,
                        method == HttpMethod.Put ? new McpApiKeyRequest { ApiKey = "k" } : null, _output, "plain " + path);
                    Assert.Equal(HttpStatusCode.Forbidden, answer.Status);
                }

                // ---- registering over HTTP does not dial, and the domain's refusals come back as 400 -------------
                var badScheme = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostUserId, HttpMethod.Post, "/api/Mcp/servers",
                    new McpServerRegistrationRequest { Name = "Bad", Endpoint = "file:///etc/passwd" }, _output, "host register bad scheme");
                Assert.Equal(HttpStatusCode.BadRequest, badScheme.Status);

                var registered = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostUserId, HttpMethod.Post, "/api/Mcp/servers",
                    new McpServerRegistrationRequest { Name = "Example", Endpoint = "https://mcp.example.test/mcp" }, _output, "host register");
                Assert.Equal(HttpStatusCode.OK, registered.Status);
                var view = JsonNode.Parse(registered.Body);
                Assert.Equal("Unverified", view?["trustState"]?.GetValue<string>());
                Assert.False(view?["hasApiKey"]?.GetValue<bool>());
                Assert.Empty(view?["tools"]?.AsArray() ?? new JsonArray());

                // The same name twice on one site is refused; the response never says what the other one is.
                var duplicate = await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostUserId, HttpMethod.Post, "/api/Mcp/servers",
                    new McpServerRegistrationRequest { Name = "Example", Endpoint = "https://other.example.test/mcp" }, _output, "host register duplicate");
                Assert.Equal(HttpStatusCode.BadRequest, duplicate.Status);

                // A key goes in and nothing brings it back out: the view says only that one exists.
                var id = view?["id"]?.GetValue<Guid>() ?? Guid.Empty;
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, hostClient, hostCookies, Host, hostUserId, HttpMethod.Put,
                    $"/api/Mcp/servers/{id}/apikey", new McpApiKeyRequest { ApiKey = "a-real-key" }, _output, "host set key")).Status);

                var after = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/api/Mcp/servers", _output, "host servers after");
                Assert.True(JsonNode.Parse(after.Body)!.AsArray()[0]?["hasApiKey"]?.GetValue<bool>());
                Assert.DoesNotContain("a-real-key", after.Body);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_holder_of_mcp_view_alone_sees_the_catalog_and_NOT_the_ledger_or_the_registrations()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output,
                colocateModuleAggregates: false, extraSettings: McpCatalogDriveTests.McpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                // THE SPLIT IS THE POINT (McpPermissions): D101 named two keys and would have gated the published
                // catalog and the AUDIT LEDGER on the same one. The catalog is what a user may use; the ledger is every
                // principal's calls on the site. One key over both is the shape D100 had to unpick in Notifications.
                //
                // A user holding NO key would answer 403 to all three and prove nothing about the split, so this user
                // holds mcp:view and only mcp:view.
                await GateDrive.EnsureRoleAsync(app, Host, GateDrive.GateRole);
                var viewer = await GateDrive.AddUserAsync(app, Host, GateDrive.GateUsername, GateDrive.GatePassword, GateDrive.GateEmail, GateDrive.GateRole);
                await GrantAsync(app, McpPermissions.View);

                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.GateUsername, GateDrive.GatePassword, _output);

                var published = await GateDrive.GetAsync(client, cookies, Host, "/api/Mcp/published", _output, "viewer published");
                Assert.Equal(HttpStatusCode.OK, published.Status);
                Assert.Contains(StommeSiteContributor.SiteUri, published.Body);

                // ...but NOT the site's ledger, and NOT its external-server registrations.
                Assert.Equal(HttpStatusCode.Forbidden,
                    (await GateDrive.GetAsync(client, cookies, Host, "/api/Mcp/audit?days=7", _output, "viewer audit")).Status);
                Assert.Equal(HttpStatusCode.Forbidden,
                    (await GateDrive.GetAsync(client, cookies, Host, "/api/Mcp/servers", _output, "viewer servers")).Status);

                // The closing control: grant the SECOND key to the same role and the ledger opens, with nothing else
                // changed. Without this, the 403 above could be anything about this user.
                await GrantAsync(app, McpPermissions.ViewAudit);
                Assert.Equal(HttpStatusCode.OK,
                    (await GateDrive.GetAsync(client, cookies, Host, "/api/Mcp/audit?days=7", _output, "viewer audit (granted)")).Status);

                // ...and mcp:manage is still its own key: the ledger opening did not open the registrations.
                Assert.Equal(HttpStatusCode.Forbidden,
                    (await GateDrive.GetAsync(client, cookies, Host, "/api/Mcp/servers", _output, "viewer servers (after audit granted)")).Status);

                _output.WriteLine($"viewer {viewer.UserId}: view=200, view-audit 403 -> 200 on its own grant, manage=403 throughout");
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>Grants one MCP key to the gate role on this site.</summary>
        private static Task<bool> GrantAsync(WebApplication app, string key)
        {
            var (resource, operation) = (McpPermissions.Resource, key.Substring(key.IndexOf(':') + 1));
            return GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var permissions = sp.GetRequiredService<IPermissionRepository>();
                var submitted = (await permissions.GetPermissionsAsync(alias.SiteId, resource, operation))
                    .Where(row => row.EntityId == PermissionKeys.KeyEntityId)
                    .ToList();
                submitted.Add(new Permission(alias.SiteId, resource, PermissionKeys.KeyEntityId, operation, GateDrive.GateRole, null, true));
                await permissions.UpdatePermissionsAsync(alias.SiteId, resource, PermissionKeys.KeyEntityId, submitted);
                return true;
            });
        }

        [Fact]
        public async Task Turning_the_mcp_server_feature_off_stops_the_catalog_WITHOUT_a_restart_and_still_audits()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output,
                colocateModuleAggregates: false, extraSettings: McpCatalogDriveTests.McpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;

                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                // ON by default: the host account is served the catalog, so the OFF assertion below means something.
                var before = await GateDrive.GetAsync(client, cookies, Host, "/api/Mcp/published", _output, "feature on");
                Assert.Equal(HttpStatusCode.OK, before.Status);
                Assert.NotEmpty(JsonNode.Parse(before.Body)!.AsArray());

                // ---- the RUNTIME kill switch, on the SAME host -----------------------------------------------------
                // Mcp:Server:Enabled is read while the container is composed, so turning THAT off needs a restart.
                // This one is decided per call: nothing is recomposed between these two requests.
                await ToggleAsync(app, enabled: false);

                var off = await GateDrive.GetAsync(client, cookies, Host, "/api/Mcp/published", _output, "feature off");
                Assert.Equal(HttpStatusCode.OK, off.Status);
                Assert.Empty(JsonNode.Parse(off.Body)!.AsArray());

                // ...and a CALL is refused and still AUDITED - a tenant switched off does not go quiet.
                var ledgerBefore = (await LedgerAsync(app, siteId)).Count;
                var refused = await McpRpc.CallAsync(client, cookies, Host, "tools/call",
                    new JsonObject { ["name"] = McpTestContributor.Echo, ["arguments"] = new JsonObject() }, _output, "feature off call");
                Assert.True(refused?["result"]?["isError"]?.GetValue<bool>());

                var ledgerAfter = await LedgerAsync(app, siteId);
                Assert.Equal(ledgerBefore + 1, ledgerAfter.Count);
                Assert.Equal(McpAuditOutcome.Denied, ledgerAfter.OrderBy(entry => entry.OccurredOnUtc).Last().Outcome);

                // ---- and back ON, again without a restart ----------------------------------------------------------
                // The closing control: only the flag changed between the empty list and this one.
                await ToggleAsync(app, enabled: true);
                var on = await GateDrive.GetAsync(client, cookies, Host, "/api/Mcp/published", _output, "feature on again");
                Assert.NotEmpty(JsonNode.Parse(on.Body)!.AsArray());
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<Guid> ToggleAsync(WebApplication app, bool enabled) =>
            GateDrive.InTenantAsync(app, Host, (sp, _) =>
                sp.GetRequiredService<Stomme.FeatureManagement.Application.IFeatureAdminService>()
                    .ToggleAsync(Stomme.FeatureManagement.FeatureCatalog.McpServer, enabled));

        private static Task<System.Collections.Generic.List<Stomme.Modules.Mcp.Domain.McpAuditEntryAggregate>> LedgerAsync(WebApplication app, int siteId) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                (await sp.GetRequiredService<Stomme.Mcp.Persistence.IMcpAuditRepository>().ListBySiteAsync(siteId)).ToList());

        [Fact]
        public async Task The_admin_page_RENDERS_on_a_server_runtime_site_which_needs_the_server_side_service()
        {
            // THE DEFECT THIS EXISTS FOR, found by reading the framework rather than by any green test: the framework
            // scans IClientService only in the WebAssembly client's Program, and the server scans IService and
            // ITransientService. A control injecting IMcpService on a site whose Runtime is Server - the DEFAULT for a
            // fresh install and for every site the admin UI creates - therefore had NOTHING TO RESOLVE, and the page
            // could not render. Every API assertion above stayed green through it, because the controller has
            // dependencies of its own. Every other in-tree module ships this twin; this one did not.
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output,
                colocateModuleAggregates: false, extraSettings: McpCatalogDriveTests.McpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                // 1. It resolves at all, and it is the SERVER implementation - not the browser's HTTP one, which would
                //    make the host call itself.
                var service = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    Task.FromResult(sp.GetService<Stomme.Modules.Mcp.Services.IMcpService>()));
                Assert.NotNull(service);
                Assert.IsType<Stomme.Modules.Mcp.Services.ServerMcpService>(service);

                // 2. And the page RENDERS, which is the bar: a 200 with the control's own markup in it.
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                var page = await GateDrive.GetAsync(client, cookies, Host, "/admin/mcp", _output, "admin/mcp render");
                Assert.Equal(HttpStatusCode.OK, page.Status);
                // The bar an admin page is held to here (InstalledSiteHealthDriveTests): served, carrying the page's
                // own <title>, and NOT the installer. The module control's inner markup is deliberately NOT
                // asserted - MEASURED: an admin pane does not emit it in SSR, so a Contains on it would fail for a
                // reason that has nothing to do with this module. The control itself is driven by CLICKING it
                // (McpControlClickTests), which is the only thing that can anyway.
                Assert.Contains("<title>MCP", page.Body);
                Assert.DoesNotContain("Installation Wizard", page.Body);
                Assert.DoesNotContain("Default Site Template", page.Body);

                // 3. The twin GATES ITSELF, because it is not behind the controller: a control resolving it in-process
                //    never passes through [RequiresPermissionKey]. A user holding none of the keys is refused here too.
                await GateDrive.EnsureRoleAsync(app, Host, GateDrive.GateRole);
                await GateDrive.AddUserAsync(app, Host, GateDrive.GateUsername, GateDrive.GatePassword, GateDrive.GateEmail, GateDrive.GateRole);

                using var plainClient = GateDrive.NewClient(app, Host);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, GateDrive.GateUsername, GateDrive.GatePassword, _output);
                var denied = await GateDrive.GetAsync(plainClient, plainCookies, Host, "/admin/mcp", _output, "admin/mcp as non-holder");

                // The page itself is Administrators-only, so a non-holder does not reach it at all - asserted as the
                // page's own title being absent, which is what this render emits. The KEY-level refusal, for a user
                // who does reach the page, is McpControlClickTests and the 403s over api/Mcp above.
                Assert.DoesNotContain("<title>MCP", denied.Body);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_both_halves_off_there_is_no_admin_page_and_no_route()
        {
            // The stock template - neither switch set. "Off means absent" is the whole posture: not a page that says
            // "disabled", and not a route that answers 403 to a caller who would be allowed if it existed.
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                Assert.Null(await PageAsync(app, "admin/mcp"));

                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                // 404, not 403: the host account HOLDS mcp:manage (the keys are declared and seeded either way - a
                // switch must not take the permission rows with it), so a 403 here would mean the route existed.
                foreach (var path in new[] { "/api/Mcp/servers", "/api/Mcp/published" })
                {
                    var answer = await GateDrive.GetAsync(client, cookies, Host, path, _output, "off " + path);
                    Assert.Equal(HttpStatusCode.NotFound, answer.Status);
                }

                // ...and the NON-vacuity control for that claim: the audit route is gated on a key and NOT on either
                // switch - the ledger is durable and readable whether or not MCP is currently switched on - so it
                // answers 200 here. If every route 404'd, the assertions above would prove nothing about the switches.
                var audit = await GateDrive.GetAsync(client, cookies, Host, "/api/Mcp/audit?days=7", _output, "off audit");
                Assert.Equal(HttpStatusCode.OK, audit.Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_a_half_switched_on_the_admin_page_is_seeded()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output,
                colocateModuleAggregates: false, extraSettings: McpCatalogDriveTests.McpEnabled());
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                var page = await PageAsync(app, "admin/mcp");
                Assert.NotNull(page);
                _output.WriteLine($"admin/mcp -> page {page!.PageId} '{page.Name}'");

                // ...and it PLACES the module, or it is an empty page with a name.
                var modules = await GateDrive.InTenantAsync(app, Host, (sp, _) =>
                    sp.GetRequiredService<IPageModuleRepository>().GetPageModulesAsync(page.SiteId));
                Assert.Contains(modules, item => item.PageId == page.PageId);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static Task<Page?> PageAsync(WebApplication app, string path) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                (await sp.GetRequiredService<IPageRepository>().GetPagesAsync(alias.SiteId))
                    .FirstOrDefault(page => string.Equals(page.Path, path, StringComparison.OrdinalIgnoreCase)));

        /// <summary>The five by-id routes, as a browser performs them.</summary>
        private static IEnumerable<(HttpMethod Method, string Path)> ByIdRoutes(Guid id) =>
        [
            (HttpMethod.Post, $"/api/Mcp/servers/{id}/refresh"),
            (HttpMethod.Post, $"/api/Mcp/servers/{id}/enable"),
            (HttpMethod.Post, $"/api/Mcp/servers/{id}/disable"),
            (HttpMethod.Delete, $"/api/Mcp/servers/{id}"),
            (HttpMethod.Put, $"/api/Mcp/servers/{id}/apikey"),
        ];

    }
}
