using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Server;
using Stomme.Shared;
using Stomme.Tests.Fixtures.GateTestModule;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// THE SITE IS THE UNIT (D99 point 10, BL-99): a permission key granted on site A must not admit the SAME
    /// user, in the SAME role, on site B of the SAME tenant - driven per alias over real HTTP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the assertion a bridge built on claims could not make honestly, and the one a row-based bridge
    /// has to earn: the rows are keyed per site, the permission cache key is alias-prefixed, and the ROLE half
    /// of the decision comes from the cookie minted at THAT site's login. So the test creates a second site in
    /// the same tenant with its own alias, puts the same user in a role of the SAME NAME on both sites, grants
    /// the key on site A only, and logs the user in separately on each alias.
    /// </para>
    /// <para>
    /// NON-VACUITY IS THE WHOLE DESIGN. Site B's 403 would prove nothing if the user simply lacked the role
    /// there, so the test asserts the role membership on BOTH sites first - and then closes the loop by
    /// granting the key on site B too and watching the same request turn 200, which can only be the row.
    /// </para>
    /// <para>
    /// PostgreSQL and RavenDB - the two stores a real deployment runs on, one relational and one document,
    /// both FAIL-NOT-SKIP when unreachable (D46). SQLite is deliberately absent: the isolation claim is about
    /// the query the provider runs, and the relational leg is worth more against the real thing.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class LiftedRequiresSiteIsolationTests
    {
        private const string HostA = "localhost";
        private const string HostB = "siteb.localhost";

        private readonly ITestOutputHelper _output;

        public LiftedRequiresSiteIsolationTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new TheoryData<string>
        {
            PostgresGateStore.StoreName,
            RavenGateStore.StoreName
        };

        [Theory]
        [MemberData(nameof(Stores))]
        public async Task A_key_granted_on_one_site_does_not_admit_the_same_user_on_another(string storeName)
        {
            var store = LiftedRequiresGateTests.ResolveStore(storeName);
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = new ScratchGateInstall(store, HostA);
            var app = await GateDrive.InstallAsync(scratch, _output);

            try
            {
                // ---- 1. A SECOND SITE IN THE SAME TENANT, with its own alias ------------------------------
                var aliasA = await GateDrive.AliasAsync(app, HostA);
                var siteB = await CreateSecondSiteAsync(app);
                var aliasB = await GateDrive.AliasAsync(app, HostB);

                _output.WriteLine($"[{store.Name}] site A = {aliasA.SiteId} (alias '{aliasA.Name}', SiteKey {aliasA.SiteKey}); " +
                    $"site B = {aliasB.SiteId} (alias '{aliasB.Name}', SiteKey {aliasB.SiteKey})");
                _output.WriteLine($"[{store.Name}] permission cache keys: " +
                    $"'SiteKey:{aliasA.SiteKey}:Permissions:{aliasA.SiteId}:gatetest' vs " +
                    $"'SiteKey:{aliasB.SiteKey}:Permissions:{aliasB.SiteId}:gatetest'");

                Assert.Equal(aliasA.TenantId, aliasB.TenantId);          // one tenant, one database
                Assert.NotEqual(aliasA.SiteId, aliasB.SiteId);           // two sites
                Assert.NotEqual(aliasA.SiteKey, aliasB.SiteKey);         // hence two cache keys
                Assert.Equal(siteB.SiteId, aliasB.SiteId);

                // ---- 1b. THE KEY REACHES A SITE CREATED AFTER THE INSTALL (F7) ----------------------------
                // MEASURED here, and worth knowing: the per-site materialisation is LAZY - a site created
                // later carries NO rows for a declared key until something READS that site's module
                // definitions, which is what runs the additive reconcile. Until then the capability is dead on
                // that site for its administrators too. Driven the first time this gate ran, site B's rows
                // were "[]" through every request below, because an API call never reads the definitions.
                var definitionOnB = await GateDrive.GateTestDefinitionAsync(app, HostB);
                var seededOnB = await GateDrive.RowsAsync(app, HostB, "gatetest", "manage");
                _output.WriteLine($"[{store.Name}] site B definition {definitionOnB.ModuleDefinitionId} rows after the first definition read = {GateDrive.Describe(seededOnB)}");
                Assert.Contains(seededOnB, row => row.RoleName == RoleNames.Admin
                    && row.EntityId == PermissionKeys.KeyEntityId
                    && row.IsAuthorized);

                // ---- 2. ONE user, in a role of the SAME NAME on BOTH sites --------------------------------
                await GateDrive.EnsureRoleAsync(app, HostA, GateDrive.GateRole);
                await GateDrive.EnsureRoleAsync(app, HostB, GateDrive.GateRole);

                var onA = await GateDrive.AddUserAsync(app, HostA, GateDrive.GateUsername, GateDrive.GatePassword, GateDrive.GateEmail, GateDrive.GateRole);
                var onB = await GateDrive.AddUserAsync(app, HostB, GateDrive.GateUsername, GateDrive.GatePassword, GateDrive.GateEmail, GateDrive.GateRole);
                Assert.Equal(onA.UserId, onB.UserId);                    // the SAME user, not a namesake

                var rolesOnA = await GateDrive.RolesAsync(app, HostA, onA.UserId);
                var rolesOnB = await GateDrive.RolesAsync(app, HostB, onA.UserId);
                _output.WriteLine($"[{store.Name}] user {onA.UserId} roles on A = {rolesOnA}; on B = {rolesOnB}");
                Assert.Contains(GateDrive.GateRole, rolesOnA);
                Assert.Contains(GateDrive.GateRole, rolesOnB);           // the 403 on B cannot be a missing role
                Assert.DoesNotContain(RoleNames.Admin, rolesOnB);

                // ---- 3. THE GRANT, ON SITE A ONLY ---------------------------------------------------------
                await GrantOnSiteAsync(app, HostA);
                _output.WriteLine($"[{store.Name}] A rows = {GateDrive.Describe(await GateDrive.RowsAsync(app, HostA, "gatetest", "manage"))}");
                _output.WriteLine($"[{store.Name}] B rows = {GateDrive.Describe(await GateDrive.RowsAsync(app, HostB, "gatetest", "manage"))}");

                var rowsOnB = await GateDrive.RowsAsync(app, HostB, "gatetest", "manage");
                Assert.DoesNotContain(rowsOnB, row => row.RoleName == GateDrive.GateRole);

                // ---- 4. THE SAME USER, LOGGED IN ON EACH ALIAS -------------------------------------------
                using var clientA = GateDrive.NewClient(app, HostA);
                using var clientB = GateDrive.NewClient(app, HostB);
                var cookiesA = await GateDrive.LoginAsync(app, clientA, HostA, GateDrive.GateUsername, GateDrive.GatePassword, _output);
                var cookiesB = await GateDrive.LoginAsync(app, clientB, HostB, GateDrive.GateUsername, GateDrive.GatePassword, _output);

                var admitted = await GateDrive.GetAsync(clientA, cookiesA, HostA, "/api/GateTest/manage", _output, store.Name + " site A");
                Assert.Equal(HttpStatusCode.OK, admitted.Status);
                Assert.Contains(GateTestController.KeyGatedMarker, admitted.Body);

                var denied = await GateDrive.GetAsync(clientB, cookiesB, HostB, "/api/GateTest/manage", _output, store.Name + " site B");
                Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
                Assert.DoesNotContain(GateTestController.KeyGatedMarker, denied.Body);

                // ...and the mediator path answers the same way on each alias.
                var before = GateTestManageHandler.Invocations;
                var dispatchB = await GateDrive.GetAsync(clientB, cookiesB, HostB, "/api/GateTest/dispatch", _output, store.Name + " site B dispatch");
                Assert.Equal(HttpStatusCode.Forbidden, dispatchB.Status);
                Assert.Equal(before, GateTestManageHandler.Invocations);

                var dispatchA = await GateDrive.GetAsync(clientA, cookiesA, HostA, "/api/GateTest/dispatch", _output, store.Name + " site A dispatch");
                Assert.Equal(HttpStatusCode.OK, dispatchA.Status);
                Assert.Equal(before + 1, GateTestManageHandler.Invocations);

                // ---- 5. THE CLOSING CONTROL: grant on B, and B turns 200 ----------------------------------
                // Without this, site B's 403 could be anything about site B (a broken alias, a failed login, a
                // missing module definition). Only the row changes here.
                await GrantOnSiteAsync(app, HostB);
                _output.WriteLine($"[{store.Name}] B rows after its own grant = {GateDrive.Describe(await GateDrive.RowsAsync(app, HostB, "gatetest", "manage"))}");

                var nowAdmitted = await GateDrive.GetAsync(clientB, cookiesB, HostB, "/api/GateTest/manage", _output, store.Name + " site B (granted)");
                Assert.Equal(HttpStatusCode.OK, nowAdmitted.Status);
                Assert.Contains(GateTestController.KeyGatedMarker, nowAdmitted.Body);

                // and site A is untouched by site B's grant
                var stillAdmitted = await GateDrive.GetAsync(clientA, cookiesA, HostA, "/api/GateTest/manage", _output, store.Name + " site A (after B granted)");
                Assert.Equal(HttpStatusCode.OK, stillAdmitted.Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>
        /// Creates a second site on the SAME tenant, through <c>ISiteRepository.AddSiteAsync</c> - the very
        /// call the Host's "Add Site" admin page makes - and gives it its own alias.
        /// </summary>
        private static Task<Site> CreateSecondSiteAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, HostA, async (sp, alias) =>
            {
                var site = await sp.GetRequiredService<ISiteRepository>().AddSiteAsync(new Site
                {
                    Name = "Gate Site B",
                    TenantId = alias.TenantId,
                    DefaultThemeType = Constants.DefaultTheme,
                    DefaultContainerType = Constants.DefaultContainer,
                    AdminContainerType = Constants.DefaultAdminContainer,
                    SiteTemplateType = Constants.DefaultSiteTemplate,
                    RenderMode = RenderModes.Static,
                    Runtime = Runtimes.Server,
                    Prerender = false,
                    Hybrid = false,
                    EnhancedNavigation = true,
                    CultureCode = "en",
                    AllowRegistration = false,
                    CaptureBrokenUrls = false,
                    VisitorTracking = false,
                    PwaIsEnabled = false
                });

                await sp.GetRequiredService<IAliasRepository>().AddAliasAsync(new Alias
                {
                    Name = HostB,
                    TenantId = alias.TenantId,
                    SiteId = site.SiteId,
                    IsDefault = true
                });

                return site;
            });

        /// <summary>Grants <c>gatetest:manage</c> to the gate role on the site the given alias resolves to.</summary>
        private static Task GrantOnSiteAsync(WebApplication app, string host) =>
            GateDrive.InTenantAsync(app, host, async (sp, alias) =>
            {
                var permissions = sp.GetRequiredService<IPermissionRepository>();
                var submitted = (await permissions.GetPermissionsAsync(alias.SiteId, "gatetest", "manage"))
                    .Where(row => row.EntityId == PermissionKeys.KeyEntityId)
                    .ToList();
                submitted.Add(new Permission(alias.SiteId, "gatetest", PermissionKeys.KeyEntityId, "manage", GateDrive.GateRole, null, true));
                await permissions.UpdatePermissionsAsync(alias.SiteId, "gatetest", PermissionKeys.KeyEntityId, submitted);
                return true;
            });
    }
}