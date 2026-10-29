using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Mediator;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Security.Abstractions;
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
    /// THE DRIVEN GATE OF THE AUTHORIZATION BRIDGE - the one test that answers "does a lifted
    /// <c>[Requires]</c> key actually DECIDE anything over real HTTP".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything else about this bridge can be green while it decides nothing: the unit suites compose the
    /// guard by hand, the reflective gates only read source, and a rename shipped broken with 452 unit
    /// tests, both conformance providers AND the render guard green. So this installs a REAL site through
    /// <see cref="Program.BuildAppAsync"/> + <c>InstallAsync</c>, logs principals in through
    /// <c>/pages/login</c>, and drives the composed production pipeline:
    /// </para>
    /// <list type="number">
    ///   <item>the module's declared key is SEEDED to Administrators on the fresh site;</item>
    ///   <item>the HOST is admitted through that seeded row (F1: not by a Host short-circuit - there is none);</item>
    ///   <item>a REGISTERED-only user is denied on both paths - the controller's
    ///     <c>[RequiresPermissionKey]</c> and the mediator's <c>[Requires]</c> - and the mediator denial is a
    ///     <b>403 ProblemDetails</b>, not the HTTP 500 a denial used to be (F5), with the handler NOT run;</item>
    ///   <item>a member of a CUSTOM role is denied until a row grants that role, then admitted - the grant
    ///     performed OVER HTTP through the admin grid's own save path;</item>
    ///   <item>the same grant REVOKED over HTTP denies again on the very next request (F13: the permission
    ///     cache key is alias-prefixed, so a write made outside the request path would not have invalidated it);</item>
    ///   <item><c>?authgatetestid=</c> cannot re-scope a key (F8) - and the same query string on the
    ///     framework's own 3-segment policy, over the SAME resource and operation, DOES re-scope it and admits
    ///     the very caller the key denies. That known-dirty control is what makes "F8 closed" a measurement
    ///     rather than a claim;</item>
    ///   <item>an ANONYMOUS caller is denied 403 on both paths.</item>
    /// </list>
    /// <para>
    /// SQLite and PostgreSQL. Both cases FAIL rather than skip when their store is unreachable: a suite
    /// that quietly shrinks reports green while proving nothing, which has shipped here before. The RavenDB
    /// case is the same scenario in <see cref="RavenLiftedRequiresGateTests"/>.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    [Trait("Category", "ExternalDatabase")]
    public class LiftedRequiresGateTests
    {
        public const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public LiftedRequiresGateTests(ITestOutputHelper output) => _output = output;

        public static TheoryData<string> Stores() => new TheoryData<string>
        {
            SqliteGateStore.StoreName,
            PostgresGateStore.StoreName
        };

        [Theory]
        [MemberData(nameof(Stores))]
        public Task A_lifted_requires_key_is_enforced_over_http(string storeName) =>
            RunAsync(ResolveStore(storeName), _output);

        internal static IGateStore ResolveStore(string storeName) => storeName switch
        {
            SqliteGateStore.StoreName => new SqliteGateStore(),
            PostgresGateStore.StoreName => new PostgresGateStore(),
            RavenGateStore.StoreName => new RavenGateStore(),
            _ => throw new System.ArgumentOutOfRangeException(nameof(storeName), storeName, "Unknown store.")
        };

        /// <summary>
        /// THE NO-REQUEST CASE, and the honest limit of the circuit claim: a gated command dispatched from a
        /// scope that has NO <c>HttpContext</c> is DENIED, never admitted.
        /// </summary>
        /// <remarks>
        /// <para>
        /// That is the shape a hosted job has - it has no principal, and granting one is a separate,
        /// security-relevant decision - and it is the shape a Blazor Server CIRCUIT's scope has once the
        /// initial HTTP request is over, because <c>IHttpContextAccessor.HttpContext</c> does not flow into
        /// the circuit.
        /// </para>
        /// <para>
        /// <b>WHAT THIS DOES NOT PROVE.</b> It does not drive a live interactive circuit: this suite has no
        /// websocket/SignalR circuit harness, and nothing here establishes one. So the measured claim is only
        /// "a scope with no HttpContext denies" - NOT "the guard saw the cookie principal inside a circuit".
        /// It cannot have, in that scope. In Stomme that is not the normal module path (a Client component
        /// calls its <c>IService</c>, which calls the controller over HTTP, where the context exists and the
        /// suite above drives it), but a SERVER-side component injecting <c>IMediator</c> directly would be
        /// denied - worth knowing before a lifted page does it.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task A_gated_command_dispatched_outside_a_request_is_denied()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            var app = await Program.BuildAppAsync(new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" });

            try
            {
                using var scope = app.Services.CreateScope();
                var guard = scope.ServiceProvider.GetRequiredService<IAccessGuard>();

                // No requirement at all is still allowed - otherwise "denied" below would mean nothing.
                Assert.True(await guard.IsAllowedAsync(null, null));

                Assert.False(await guard.IsAllowedAsync(null, GateTestPermissions.Manage));
                Assert.False(await guard.IsAllowedAsync(typeof(GateTestManageCommand)));

                var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
                var denied = await Assert.ThrowsAsync<AccessDeniedException>(() => mediator.Send(new GateTestManageCommand()));
                _output.WriteLine($"[no request] dispatch -> {denied.GetType().Name}: {denied.Message}");
                Assert.Contains(nameof(GateTestManageCommand), denied.Message);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
        /// <summary>
        /// The whole scenario, store-parameterised so the RavenDB suite drives the IDENTICAL assertions rather
        /// than a paraphrase of them. Any divergence between the providers therefore shows up as a failure in
        /// one suite and not the other, which is the only way a parity claim means anything.
        /// </summary>
        internal static async Task RunAsync(IGateStore store, ITestOutputHelper output)
        {
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, output);
            var app = await GateDrive.InstallAsync(scratch, output);

            try
            {
                // ---- 1. THE DECLARATION, SEEDED ON THE FRESH SITE ------------------------------------------
                var definition = await GateDrive.GateTestDefinitionAsync(app, Host);
                output.WriteLine($"[{store.Name}] definition {definition.ModuleDefinitionId} '{definition.Name}' declares '{definition.PermissionNames}'");
                output.WriteLine($"[{store.Name}] gatetest:manage rows = {GateDrive.Describe(await GateDrive.RowsAsync(app, Host, "gatetest", "manage"))}");

                Assert.Contains(definition.PermissionList, row => row.EntityName == "gatetest"
                    && row.PermissionName == "manage"
                    && row.EntityId == PermissionKeys.KeyEntityId
                    && row.RoleName == RoleNames.Admin
                    && row.IsAuthorized);

                // An id no definition can own, so the re-scoping probe below cannot accidentally address the
                // definition's own rows.
                var rescopeId = definition.ModuleDefinitionId + 1000;

                // ---- 2. THE PRINCIPALS (roles BEFORE login - they are baked into the cookie) ---------------
                await GateDrive.EnsureRoleAsync(app, Host, GateDrive.GateRole);
                var gateUser = await GateDrive.AddUserAsync(app, Host, GateDrive.GateUsername, GateDrive.GatePassword, GateDrive.GateEmail, GateDrive.GateRole);
                await GateDrive.AddUserAsync(app, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, GateDrive.PlainEmail);

                var hostUserId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var gateRoles = await GateDrive.RolesAsync(app, Host, gateUser.UserId);
                output.WriteLine($"[{store.Name}] {GateDrive.GateUsername} roles = {gateRoles}");
                Assert.Contains(GateDrive.GateRole, gateRoles);
                Assert.DoesNotContain(RoleNames.Admin, gateRoles);
                Assert.DoesNotContain(RoleNames.Host, gateRoles);

                using var hostClient = GateDrive.NewClient(app, Host);
                using var gateClient = GateDrive.NewClient(app, Host);
                using var plainClient = GateDrive.NewClient(app, Host);
                using var anonymousClient = GateDrive.NewClient(app, Host);

                var hostCookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var gateCookies = await GateDrive.LoginAsync(app, gateClient, Host, GateDrive.GateUsername, GateDrive.GatePassword, output);
                var plainCookies = await GateDrive.LoginAsync(app, plainClient, Host, GateDrive.PlainUsername, GateDrive.PlainPassword, output);

                // ---- 3. HOST -> 200, through the SEEDED Administrators row (F1) ----------------------------
                var response = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/api/GateTest/manage", output, store.Name + " host key");
                Assert.Equal(HttpStatusCode.OK, response.Status);
                Assert.Contains(GateTestController.KeyGatedMarker, response.Body);

                var before = GateTestManageHandler.Invocations;
                response = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/api/GateTest/dispatch", output, store.Name + " host dispatch");
                Assert.Equal(HttpStatusCode.OK, response.Status);
                Assert.Contains(GateTestManageHandler.Marker, response.Body);
                Assert.Equal(before + 1, GateTestManageHandler.Invocations);

                // ---- 4. REGISTERED-ONLY -> 403 on BOTH paths, ProblemDetails, handler never runs (F5) -----
                before = GateTestManageHandler.Invocations;
                response = await GateDrive.GetAsync(plainClient, plainCookies, Host, "/api/GateTest/manage", output, store.Name + " plain key");
                Assert.Equal(HttpStatusCode.Forbidden, response.Status);
                Assert.DoesNotContain(GateTestController.KeyGatedMarker, response.Body);

                response = await GateDrive.GetAsync(plainClient, plainCookies, Host, "/api/GateTest/dispatch", output, store.Name + " plain dispatch");
                Assert.Equal(HttpStatusCode.Forbidden, response.Status);
                Assert.Equal("application/problem+json", response.ContentType);
                Assert.DoesNotContain(GateTestManageHandler.Marker, response.Body);
                Assert.Equal(before, GateTestManageHandler.Invocations);

                // ---- 5. THE CUSTOM ROLE, BEFORE ANY ROW GRANTS IT -> 403 (deny when no row, not fallback) --
                // Non-vacuity: without this, the 200 after the grant could be ambient rather than the grant.
                response = await GateDrive.GetAsync(gateClient, gateCookies, Host, "/api/GateTest/manage", output, store.Name + " gate key (ungranted)");
                Assert.Equal(HttpStatusCode.Forbidden, response.Status);

                // ---- 6. GRANT OVER HTTP, through the admin grid's own save path ----------------------------
                var granted = GateDrive.Clone(definition);
                granted.PermissionList.Add(new Permission(definition.SiteId, "gatetest", PermissionKeys.KeyEntityId, "manage", GateDrive.GateRole, null, true));
                Assert.Equal(HttpStatusCode.OK,
                    await GateDrive.PutDefinitionAsync(app, hostClient, hostCookies, Host, hostUserId, granted, output, store.Name + " grant"));
                output.WriteLine($"[{store.Name}] gatetest:manage rows after grant = {GateDrive.Describe(await GateDrive.RowsAsync(app, Host, "gatetest", "manage"))}");

                // ---- 7. THE GRANTED ROLE -> 200 on both paths, handler RAN ---------------------------------
                response = await GateDrive.GetAsync(gateClient, gateCookies, Host, "/api/GateTest/manage", output, store.Name + " gate key (granted)");
                Assert.Equal(HttpStatusCode.OK, response.Status);
                Assert.Contains(GateTestController.KeyGatedMarker, response.Body);

                before = GateTestManageHandler.Invocations;
                response = await GateDrive.GetAsync(gateClient, gateCookies, Host, "/api/GateTest/dispatch", output, store.Name + " gate dispatch");
                Assert.Equal(HttpStatusCode.OK, response.Status);
                Assert.Contains(GateTestManageHandler.Marker, response.Body);
                Assert.Equal(before + 1, GateTestManageHandler.Invocations);

                // ---- 8. F8: THE KEY PATH IGNORES ?auth<entity>id, THE 3-SEGMENT POLICY DOES NOT ------------
                // The known-dirty input: a row at a DIFFERENT EntityId that grants every Registered user. It is
                // exactly what a caller would point the framework's dynamic policy at.
                await GrantAtEntityIdAsync(app, Host, rescopeId);
                output.WriteLine($"[{store.Name}] gatetest:manage rows after the re-scope seed = {GateDrive.Describe(await GateDrive.RowsAsync(app, Host, "gatetest", "manage"))}");

                response = await GateDrive.GetAsync(plainClient, plainCookies, Host, "/api/GateTest/dynamic", output, store.Name + " plain dynamic");
                Assert.Equal(HttpStatusCode.Forbidden, response.Status);

                // The refuted "controllers get the bridge for zero new code" path: the CALLER moves the lookup.
                response = await GateDrive.GetAsync(plainClient, plainCookies, Host, $"/api/GateTest/dynamic?authgatetestid={rescopeId}", output, store.Name + " plain dynamic re-scoped");
                Assert.Equal(HttpStatusCode.OK, response.Status);
                Assert.Contains(GateTestController.DynamicGatedMarker, response.Body);

                // ...and the same query string against the KEY requirement changes nothing: still denied.
                response = await GateDrive.GetAsync(plainClient, plainCookies, Host, $"/api/GateTest/manage?authgatetestid={rescopeId}", output, store.Name + " plain key re-scoped");
                Assert.Equal(HttpStatusCode.Forbidden, response.Status);
                Assert.DoesNotContain(GateTestController.KeyGatedMarker, response.Body);

                // ...nor for a caller the key ADMITS: the answer is the -1 rows either way.
                response = await GateDrive.GetAsync(gateClient, gateCookies, Host, $"/api/GateTest/manage?authgatetestid={rescopeId}", output, store.Name + " gate key re-scoped");
                Assert.Equal(HttpStatusCode.OK, response.Status);

                // ---- 9. ANONYMOUS -> 403 on both paths ----------------------------------------------------
                before = GateTestManageHandler.Invocations;
                response = await GateDrive.GetAsync(anonymousClient, null, Host, "/api/GateTest/manage", output, store.Name + " anonymous key");
                Assert.Equal(HttpStatusCode.Forbidden, response.Status);

                response = await GateDrive.GetAsync(anonymousClient, null, Host, "/api/GateTest/dispatch", output, store.Name + " anonymous dispatch");
                Assert.Equal(HttpStatusCode.Forbidden, response.Status);
                Assert.Equal(before, GateTestManageHandler.Invocations);

                // ---- 10. REVOKE OVER HTTP -> denied on the very next request (F13) -------------------------
                Assert.Equal(HttpStatusCode.OK,
                    await GateDrive.PutDefinitionAsync(app, hostClient, hostCookies, Host, hostUserId, GateDrive.Clone(definition), output, store.Name + " revoke"));
                var afterRevoke = await GateDrive.RowsAsync(app, Host, "gatetest", "manage");
                output.WriteLine($"[{store.Name}] gatetest:manage rows after revoke = {GateDrive.Describe(afterRevoke)}");
                Assert.DoesNotContain(afterRevoke, row => row.RoleName == GateDrive.GateRole && row.EntityId == PermissionKeys.KeyEntityId);

                response = await GateDrive.GetAsync(gateClient, gateCookies, Host, "/api/GateTest/manage", output, store.Name + " gate key (revoked)");
                Assert.Equal(HttpStatusCode.Forbidden, response.Status);

                before = GateTestManageHandler.Invocations;
                response = await GateDrive.GetAsync(gateClient, gateCookies, Host, "/api/GateTest/dispatch", output, store.Name + " gate dispatch (revoked)");
                Assert.Equal(HttpStatusCode.Forbidden, response.Status);
                Assert.Equal(before, GateTestManageHandler.Invocations);

                // ...and the Administrators row was NOT collateral damage: the host still holds the key.
                response = await GateDrive.GetAsync(hostClient, hostCookies, Host, "/api/GateTest/manage", output, store.Name + " host key (after revoke)");
                Assert.Equal(HttpStatusCode.OK, response.Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>
        /// Writes a <c>(gatetest, entityId, manage)</c> row granting <see cref="RoleNames.Registered"/> - the
        /// row a caller would re-scope the framework's dynamic policy onto. Deliberately NOT at
        /// <see cref="PermissionKeys.KeyEntityId"/>: the key's own answer must be unaffected by it.
        /// </summary>
        private static Task GrantAtEntityIdAsync(WebApplication app, string host, int entityId) =>
            GateDrive.InTenantAsync(app, host, async (sp, alias) =>
            {
                // Written through AddPermissionAsync, NOT UpdatePermissionsAsync, and that is a measurement
                // rather than a preference: UpdatePermissionsAsync stamps every submitted row whose EntityName
                // equals the entity being written with THAT EntityId, and its delete pass considers existing
                // rows at either that EntityId or -1 - so writing an instance-scoped row for a resource that
                // also carries key rows DELETES the key's -1 rows, and they cannot be preserved by
                // resubmitting them either (they would be restamped). Driven: the first run of this gate wrote
                // the 1003 row through UpdatePermissionsAsync and the key rows vanished ("rows after the
                // re-scope seed = [gatetest:manage:Registered Users=True(entityId 1003)]"), taking the grant
                // down with them. Worth knowing before a lifted module writes instance rows under a key's
                // resource - it can silently revoke the key for the whole site.
                var registered = (await sp.GetRequiredService<IRoleRepository>().GetRolesAsync(alias.SiteId, true))
                    .Single(role => role.Name == RoleNames.Registered);

                await sp.GetRequiredService<IPermissionRepository>().AddPermissionAsync(new Permission
                {
                    SiteId = alias.SiteId,
                    EntityName = "gatetest",
                    EntityId = entityId,
                    PermissionName = "manage",
                    RoleId = registered.RoleId,
                    IsAuthorized = true
                });
                return true;
            });
    }
}
