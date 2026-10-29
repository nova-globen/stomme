using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Managers;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Server;
using Stomme.Shared;
using Stomme.Tests.Fixtures.GateTestModule;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The module-definition PUT must accept only the FOREIGN-entity permission rows that definition itself
    /// declares, and must not let a save lock the Administrators role out of a declared key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The escalation this closes, driven.</b> <c>ModuleDefinitionController.Put</c> is
    /// <c>[Authorize(Roles = Administrators)]</c> and hands the submitted <c>PermissionList</c> straight to
    /// <c>UpdatePermissionsAsync</c>, which stamps any row whose EntityName differs from
    /// <c>ModuleDefinition</c> with EntityId -1 - i.e. writes a SITE-WIDE permission row for an arbitrary
    /// entity. A site administrator could therefore grant themselves the framework's own
    /// <c>User:Write</c>-style API policies through a module's own editor. That surface pre-dates the bridge;
    /// the first case below drives it as a KNOWN-DIRTY input, so the test would have failed before the fix and
    /// passes after it.
    /// </para>
    /// <para>
    /// The second case is the non-vacuity control (a DECLARED foreign row still saves), and the third is the
    /// server-side mirror of the grid's own lockout rule: the Administrators column is DISABLED for a non-host,
    /// so a submission that drops it did not come from the grid - the row is put back rather than deleted.
    /// </para>
    /// <para>
    /// Driven over real HTTP through the composed host with a matched antiforgery pair, as a REGISTERED user
    /// holding the Administrators role and NOT the Host role - the principal the escalation belongs to.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class ModuleDefinitionPermissionWriteValidationTests
    {
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private const string TenantHost = "localhost";
        private const string HostUsername = "host";
        private const string HostPassword = "H0st!Write$2026";
        private const string HostEmail = "host@write.test";

        private const string SiteAdminUsername = "siteadmin";
        private const string SiteAdminPassword = "S1te!Admin$2026";
        private const string SiteAdminEmail = "siteadmin@write.test";

        private const string GateRole = "GateRole";

        private readonly ITestOutputHelper _output;

        public ModuleDefinitionPermissionWriteValidationTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task An_undeclared_foreign_permission_row_is_rejected_and_a_declared_one_still_saves()
        {
            using var scratch = new ScratchInstall();
            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                var installTask = Task.Run(() => Program.InstallAsync(app));
                Assert.True(await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) == installTask, "The installer did not finish in time.");
                var install = await installTask;
                Assert.True(install.Success, $"Installer failed: {install.Message}");

                await app.StartAsync();
                var server = app.GetTestServer();

                var definition = await GateTestDefinitionAsync(app);
                _output.WriteLine($"target definition {definition.ModuleDefinitionId} '{definition.Name}' declares '{definition.PermissionNames}'");
                Assert.Contains(definition.PermissionList, row => row.EntityName == "gatetest" && row.PermissionName == "manage" && row.RoleName == RoleNames.Admin);

                await CreateSiteAdminAsync(app);
                var adminUserId = await UserIdAsync(app, SiteAdminUsername);
                var cookies = new CookieContainer();

                using var client = new HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://" + TenantHost) };
                await LoginAsync(app, client, cookies, SiteAdminUsername, SiteAdminPassword);
                AssertIdentityCookie(cookies);

                // ---- 1. KNOWN-DIRTY: an UNDECLARED foreign row (the framework's own User:Write policy) ----
                var escalation = Clone(definition);
                escalation.PermissionList.Add(new Permission(definition.SiteId, EntityNames.User, PermissionKeys.KeyEntityId, PermissionNames.Write, RoleNames.Admin, null, true));

                var status = await PutDefinitionAsync(app, client, cookies, adminUserId, escalation);
                _output.WriteLine($"[escalation] PUT /api/ModuleDefinition/{definition.ModuleDefinitionId} -> {(int)status}");
                Assert.Equal(HttpStatusCode.BadRequest, status);

                var userWrite = await RowsAsync(app, EntityNames.User, PermissionNames.Write);
                _output.WriteLine($"[escalation] User:Write rows afterwards = {Describe(userWrite)}");
                Assert.DoesNotContain(userWrite, row => row.EntityId == PermissionKeys.KeyEntityId);

                // ---- 2. NON-VACUITY: the same PUT with only DECLARED rows succeeds -------------------------
                var legitimate = Clone(definition);
                status = await PutDefinitionAsync(app, client, cookies, adminUserId, legitimate);
                _output.WriteLine($"[declared] PUT -> {(int)status}");
                Assert.Equal(HttpStatusCode.OK, status);

                var manage = await RowsAsync(app, "gatetest", "manage");
                _output.WriteLine($"[declared] gatetest:manage rows afterwards = {Describe(manage)}");
                Assert.Contains(manage, row => row.RoleName == RoleNames.Admin && row.IsAuthorized);
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_non_host_save_that_drops_the_administrators_grant_does_not_lock_administrators_out()
        {
            using var scratch = new ScratchInstall();
            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                var installTask = Task.Run(() => Program.InstallAsync(app));
                Assert.True(await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) == installTask, "The installer did not finish in time.");
                var install = await installTask;
                Assert.True(install.Success, $"Installer failed: {install.Message}");

                await app.StartAsync();
                var server = app.GetTestServer();

                // A custom-role grant on the SAME key first. Without it the submission would carry no row at
                // all for (gatetest, manage), and UpdatePermissionsAsync only ever deletes within the
                // (EntityName, PermissionName) pairs the submission mentions - so the grant would survive by
                // accident and this test would pass while proving nothing (measured: it did).
                await GrantToCustomRoleAsync(app, "gatetest", "manage");

                var definition = await GateTestDefinitionAsync(app);
                await CreateSiteAdminAsync(app);
                var adminUserId = await UserIdAsync(app, SiteAdminUsername);
                var cookies = new CookieContainer();

                using var client = new HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://" + TenantHost) };
                await LoginAsync(app, client, cookies, SiteAdminUsername, SiteAdminPassword);

                // The grid DISABLES the Administrators column for a non-host, so a submission without it is a
                // crafted one. Server-side it must not be able to delete the grant.
                var lockout = Clone(definition);
                var dropped = lockout.PermissionList
                    .Where(row => row.EntityName == "gatetest" && row.PermissionName == "manage" && row.RoleName == RoleNames.Admin)
                    .ToList();
                Assert.NotEmpty(dropped);
                foreach (var row in dropped)
                {
                    lockout.PermissionList.Remove(row);
                }

                // The submission still mentions (gatetest, manage) through the custom role, which is what makes
                // the Administrators row a DELETE candidate.
                Assert.Contains(lockout.PermissionList, row => row.EntityName == "gatetest" && row.PermissionName == "manage" && row.RoleName == GateRole);

                var status = await PutDefinitionAsync(app, client, cookies, adminUserId, lockout);
                _output.WriteLine($"[lockout] PUT -> {(int)status}");

                var manage = await RowsAsync(app, "gatetest", "manage");
                _output.WriteLine($"[lockout] gatetest:manage rows afterwards = {Describe(manage)}");
                Assert.Contains(manage, row => row.RoleName == RoleNames.Admin && row.IsAuthorized);
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        /// <summary>
        /// The framework's OWN API policies must be untouchable through a module's editor: a plain save must
        /// MATERIALISE none of them, and a crafted save must not GRANT one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Both halves were driven on the review branch and both were open. The stock Users definition declares
        /// <c>User:Write:Administrators</c> and <c>UserRole:Write:Administrators</c> - 3-segment entries that
        /// have carried framework API policies since long before this bridge and that deliberately have NO
        /// Permission rows: <c>UserPermissions.IsAuthorizedAsync(..., roles)</c> falls back to role membership
        /// exactly while the row list is EMPTY. Materialising one row therefore flips the whole pair from
        /// role-decided to row-decided, and the Host-only <c>[Authorize(Policy = "User:Write:Host Users")]</c>
        /// on <c>UserController.Delete</c> starts admitting whoever the row names.
        /// </para>
        /// <para>
        /// Phase 1 is the no-op save (the Save button, nothing edited) - it must write nothing at all. Phase 2
        /// is the crafted grant to <see cref="RoleNames.Everyone"/>, which would make the Host-only endpoint
        /// reachable ANONYMOUSLY. Phase 3 is the shape the admin grid itself produced for a HOST saver, which
        /// is the mirror-image failure: a <c>User:Write:Host Users</c> row as the ONLY row for the pair locks
        /// site Administrators OUT of user management. A declaration is NOT a licence to write the row.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task The_module_definition_put_cannot_materialise_or_grant_a_framework_api_policy()
        {
            using var scratch = new ScratchInstall();
            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                var installTask = Task.Run(() => Program.InstallAsync(app));
                Assert.True(await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) == installTask, "The installer did not finish in time.");
                var install = await installTask;
                Assert.True(install.Success, $"Installer failed: {install.Message}");

                await app.StartAsync();
                var server = app.GetTestServer();

                var users = await DefinitionAsync(app, "Users");
                _output.WriteLine($"stock 'Users' definition {users.ModuleDefinitionId} declares '{users.PermissionNames}'");
                _output.WriteLine($"its merged PermissionList = {Describe(users.PermissionList)}");

                // The definition's own rows are ModuleDefinition rows (which is itself an EntityNames
                // constant); nothing else belongs in the list. Phase 4 below is the non-vacuous half.
                Assert.DoesNotContain(users.PermissionList, row => row.EntityName != EntityNames.ModuleDefinition);

                await CreateSiteAdminAsync(app);
                var adminUserId = await UserIdAsync(app, SiteAdminUsername);
                var victim = await CreateUserAsync(app, "victim");
                var cookies = new CookieContainer();

                using var client = new HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://" + TenantHost) };
                await LoginAsync(app, client, cookies, SiteAdminUsername, SiteAdminPassword);
                AssertIdentityCookie(cookies);

                var before = await RowsAsync(app, EntityNames.User, PermissionNames.Write);
                _output.WriteLine($"[before] (User,Write) rows = {Describe(before)}");
                Assert.Empty(before);

                var delete = await DeleteUserAsync(app, client, cookies, adminUserId, victim, users.SiteId);
                _output.WriteLine($"[before] DELETE /api/User/{victim} -> {(int)delete}");
                Assert.Equal(HttpStatusCode.Forbidden, delete);

                // ---- 1. a PLAIN, UNCHANGED save materialises nothing -----------------------------------
                var status = await PutDefinitionAsync(app, client, cookies, adminUserId, Clone(users));
                _output.WriteLine($"[plain save] PUT /api/ModuleDefinition/{users.ModuleDefinitionId} -> {(int)status}");
                Assert.Equal(HttpStatusCode.OK, status);

                var afterSave = await RowsAsync(app, EntityNames.User, PermissionNames.Write);
                _output.WriteLine($"[plain save] (User,Write) rows = {Describe(afterSave)}");
                Assert.Empty(afterSave);

                delete = await DeleteUserAsync(app, client, cookies, adminUserId, victim, users.SiteId);
                _output.WriteLine($"[plain save] DELETE /api/User/{victim} -> {(int)delete}");
                Assert.Equal(HttpStatusCode.Forbidden, delete);
                Assert.True(await UserExistsAsync(app, "victim"), "the Host-only user DELETE succeeded for a site administrator");

                // ---- 2. a CRAFTED grant of a DECLARED framework policy is a 400 ------------------------
                var everyone = Clone(users);
                everyone.PermissionList.Add(new Permission(users.SiteId, EntityNames.User, PermissionKeys.KeyEntityId, PermissionNames.Write, RoleNames.Everyone, null, true));
                status = await PutDefinitionAsync(app, client, cookies, adminUserId, everyone);
                _output.WriteLine($"[grant to '{RoleNames.Everyone}'] PUT -> {(int)status}");
                Assert.Equal(HttpStatusCode.BadRequest, status);

                var afterGrant = await RowsAsync(app, EntityNames.User, PermissionNames.Write);
                _output.WriteLine($"[grant to '{RoleNames.Everyone}'] (User,Write) rows = {Describe(afterGrant)}");
                Assert.Empty(afterGrant);

                // ---- 3. the HOST-SAVE shape the grid produced is a 400 too -----------------------------
                var hostShape = Clone(users);
                hostShape.PermissionList.Add(new Permission(users.SiteId, EntityNames.User, PermissionKeys.KeyEntityId, PermissionNames.Write, RoleNames.Host, null, true));
                hostShape.PermissionList.Add(new Permission(users.SiteId, EntityNames.UserRole, PermissionKeys.KeyEntityId, PermissionNames.Write, RoleNames.Host, null, true));
                status = await PutDefinitionAsync(app, client, cookies, adminUserId, hostShape);
                _output.WriteLine($"[host-grid shape] PUT -> {(int)status}");
                Assert.Equal(HttpStatusCode.BadRequest, status);

                var afterHostShape = await RowsAsync(app, EntityNames.User, PermissionNames.Write);
                _output.WriteLine($"[host-grid shape] (User,Write) rows = {Describe(afterHostShape)}");

                // Zero rows IS the Administrators fallback: with no row for the pair the 6-argument overload
                // decides by role membership, which is what user management has always relied on.
                Assert.Empty(afterHostShape);
                Assert.Empty(await RowsAsync(app, EntityNames.UserRole, PermissionNames.Write));

                // ---- 4. the read-path merge must not drag a framework row into the definition's list -----
                // Written DIRECTLY (the PUT above now refuses it), which is how an upgrade or another tool
                // could put one there. The list it would join is consumed by IsAuthorizedAsync(User,
                // PermissionNames.Utilize, PermissionList), which filters on PermissionName ALONE.
                await GrantToCustomRoleAsync(app, EntityNames.User, PermissionNames.Write);
                _output.WriteLine($"[merge] (User,Write) rows = {Describe(await RowsAsync(app, EntityNames.User, PermissionNames.Write))}");

                var merged = await DefinitionAsync(app, "Users");
                _output.WriteLine($"[merge] the definition's PermissionList = {Describe(merged.PermissionList)}");
                Assert.DoesNotContain(merged.PermissionList, row => row.EntityName != EntityNames.ModuleDefinition);
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }

        /// <summary>
        /// Clearing every cell of a declared KEY in the grid must actually revoke it (an operator action that
        /// makes it load-bearing), and the lazy additive reconcile's answer to a deliberate revocation is observed
        /// here rather than assumed.
        /// </summary>
        /// <remarks>
        /// <c>UpdatePermissionsAsync</c> only ever loads - and therefore only ever deletes within - the
        /// <c>(EntityName, PermissionName)</c> pairs the SUBMISSION mentions. A submission that clears every
        /// cell of a key mentions no row for that pair at all, so the delete pass never sees the surviving
        /// grants and the last grant silently persists (and <c>modified</c> stays false, so the Permissions
        /// cache is not even cleared). The definition PUT therefore has to revoke a cleared key explicitly.
        /// </remarks>
        [Fact]
        public async Task Clearing_every_cell_of_a_declared_key_revokes_it()
        {
            using var scratch = new ScratchInstall();
            var app = await Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

            try
            {
                var installTask = Task.Run(() => Program.InstallAsync(app));
                Assert.True(await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) == installTask, "The installer did not finish in time.");
                var install = await installTask;
                Assert.True(install.Success, $"Installer failed: {install.Message}");

                await app.StartAsync();
                var server = app.GetTestServer();

                await CreateSiteAdminAsync(app);
                var adminUserId = await UserIdAsync(app, SiteAdminUsername);
                var cookies = new CookieContainer();

                using var client = new HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://" + TenantHost) };
                await LoginAsync(app, client, cookies, SiteAdminUsername, SiteAdminPassword);
                AssertIdentityCookie(cookies);

                // The control: the seeded Administrators grant is what admits the site admin at the key gate.
                var granted = await SendAsync(app, client, cookies, adminUserId, HttpMethod.Get, "/api/GateTest/manage");
                _output.WriteLine($"[granted] GET /api/GateTest/manage -> {(int)granted.Status} '{granted.Body}'");
                Assert.Equal(HttpStatusCode.OK, granted.Status);
                Assert.Contains(GateTestController.KeyGatedMarker, granted.Body);

                var definition = await DefinitionAsync(app, "GateTest");
                var cleared = Clone(definition);
                var removed = cleared.PermissionList.Where(row => row.EntityName == "gatetest").ToList();
                Assert.NotEmpty(removed);
                foreach (var row in removed)
                {
                    cleared.PermissionList.Remove(row);
                }
                _output.WriteLine($"[clear] submitting {Describe(cleared.PermissionList)} (removed {Describe(removed)})");

                var status = await PutDefinitionAsync(app, client, cookies, adminUserId, cleared);
                _output.WriteLine($"[clear] PUT -> {(int)status}");
                Assert.Equal(HttpStatusCode.OK, status);

                var rows = await RowsAsync(app, "gatetest", "manage");
                _output.WriteLine($"[clear] gatetest:manage rows = {Describe(rows)}");
                Assert.DoesNotContain(rows, row => row.IsAuthorized);

                var denied = await SendAsync(app, client, cookies, adminUserId, HttpMethod.Get, "/api/GateTest/manage");
                _output.WriteLine($"[clear] GET /api/GateTest/manage -> {(int)denied.Status}");
                Assert.Equal(HttpStatusCode.Forbidden, denied.Status);

                // The revocation must SURVIVE the lazy additive reconciliation, which cannot tell "never
                // seeded" from "deliberately revoked" and re-seeds the first one. With the grants merely
                // deleted this measured 403 then 200 again after one definitions read; the deny row is what
                // makes the state representable.
                var count = await ReadDefinitionsAsync(app);
                var reconciled = await RowsAsync(app, "gatetest", "manage");
                _output.WriteLine($"[after the next definitions read ({count} definitions)] gatetest:manage rows = {Describe(reconciled)}");
                Assert.DoesNotContain(reconciled, row => row.IsAuthorized);

                var stillDenied = await SendAsync(app, client, cookies, adminUserId, HttpMethod.Get, "/api/GateTest/manage");
                _output.WriteLine($"[after the next definitions read] GET /api/GateTest/manage -> {(int)stillDenied.Status}");
                Assert.Equal(HttpStatusCode.Forbidden, stillDenied.Status);

                // ... and it must be LIFTABLE, or "revoked" would mean "destroyed". Ticking the Administrators
                // cell again is a submission that names the pair, which flips the deny row back to a grant.
                var restored = await DefinitionAsync(app, "GateTest");
                restored.PermissionList.RemoveAll(row => row.EntityName == "gatetest" && row.PermissionName == "manage");
                restored.PermissionList.Add(new Permission(restored.SiteId, "gatetest", PermissionKeys.KeyEntityId, "manage", RoleNames.Admin, null, true));
                status = await PutDefinitionAsync(app, client, cookies, adminUserId, restored);
                _output.WriteLine($"[re-grant] PUT -> {(int)status}");
                Assert.Equal(HttpStatusCode.OK, status);

                var regranted = await SendAsync(app, client, cookies, adminUserId, HttpMethod.Get, "/api/GateTest/manage");
                _output.WriteLine($"[re-grant] gatetest:manage rows = {Describe(await RowsAsync(app, "gatetest", "manage"))}");
                _output.WriteLine($"[re-grant] GET /api/GateTest/manage -> {(int)regranted.Status}");
                Assert.Equal(HttpStatusCode.OK, regranted.Status);
            }
            finally
            {
                await SafeShutdownAsync(app);
            }
        }
        // ---- helpers ---------------------------------------------------------------------------------------

        private static string Describe(IEnumerable<Permission> rows) =>
            "[" + string.Join(", ", rows.Select(row => $"{row.EntityName}:{row.PermissionName}:{row.RoleName ?? ("user " + row.UserId)}={row.IsAuthorized}(entityId {row.EntityId})")) + "]";

        private static ModuleDefinition Clone(ModuleDefinition definition)
        {
            var clone = JsonSerializer.Deserialize<ModuleDefinition>(JsonSerializer.Serialize(definition));
            clone.PermissionList = definition.PermissionList.Select(row => row.Clone()).ToList();
            return clone;
        }

        private static async Task<T> InTenantAsync<T>(WebApplication app, Func<IServiceProvider, Alias, Task<T>> work)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost)
                ?? throw new InvalidOperationException($"No alias for '{TenantHost}'.");
            await sp.GetRequiredService<ITenantManager>().SetAliasAsync(alias);
            return await work(sp, alias);
        }

        private static Task<ModuleDefinition> GateTestDefinitionAsync(WebApplication app) => DefinitionAsync(app, "GateTest");

        private static Task<ModuleDefinition> DefinitionAsync(WebApplication app, string name) =>
            InTenantAsync(app, async (sp, alias) =>
            {
                var definitions = await sp.GetRequiredService<IModuleDefinitionRepository>().GetModuleDefinitionsAsync(alias.SiteId);
                return definitions.Single(item => item.Name == name);
            });

        /// <summary>Reads the site's module definitions - the path the LAZY key reconciliation runs on.</summary>
        private static Task<int> ReadDefinitionsAsync(WebApplication app) =>
            InTenantAsync(app, async (sp, alias) =>
            {
                var definitions = await sp.GetRequiredService<IModuleDefinitionRepository>().GetModuleDefinitionsAsync(alias.SiteId);
                return definitions.Count();
            });

        private static Task<int> CreateUserAsync(WebApplication app, string username) =>
            InTenantAsync(app, async (sp, alias) =>
            {
                var created = await sp.GetRequiredService<IUserManager>().AddUser(new User
                {
                    Username = username,
                    Password = "V1ctim!Pass$2026",
                    Email = username + "@write.test",
                    DisplayName = username,
                    SiteId = alias.SiteId,
                    EmailConfirmed = true
                });
                Assert.NotNull(created);
                return created.UserId;
            });

        private static Task<bool> UserExistsAsync(WebApplication app, string username) =>
            InTenantAsync(app, async (sp, alias) => await sp.GetRequiredService<IUserRepository>().GetUserAsync(username) != null);

        /// <summary>Grants a declared key to a NEW custom role, through the path the admin grid saves by.</summary>
        private static Task GrantToCustomRoleAsync(WebApplication app, string resource, string operation) =>
            InTenantAsync(app, async (sp, alias) =>
            {
                var roles = sp.GetRequiredService<IRoleRepository>();
                var role = (await roles.GetRolesAsync(alias.SiteId)).FirstOrDefault(item => item.Name == GateRole)
                    ?? await roles.AddRoleAsync(new Role { SiteId = alias.SiteId, Name = GateRole, Description = "BL-99 operator role", IsAutoAssigned = false, IsSystem = false });

                var permissions = sp.GetRequiredService<IPermissionRepository>();
                var submitted = (await permissions.GetPermissionsAsync(alias.SiteId, resource, operation)).ToList();
                submitted.Add(new Permission(alias.SiteId, resource, PermissionKeys.KeyEntityId, operation, role.Name, null, true));
                await permissions.UpdatePermissionsAsync(alias.SiteId, resource, PermissionKeys.KeyEntityId, submitted);

                // Writing a permission row directly invalidates the Permissions cache but not the
                // ModuleDefinitions one, which holds the MERGED foreign rows - the definition PUT clears it for
                // itself, so a grant made outside that path must clear it here or the next read is stale.
                await sp.GetRequiredService<ICacheManager>().RemoveCacheAsync(alias, "ModuleDefinitions");
                return true;
            });

        private static Task<List<Permission>> RowsAsync(WebApplication app, string entityName, string permissionName) =>
            InTenantAsync(app, async (sp, alias) =>
            {
                var permissions = await sp.GetRequiredService<IPermissionRepository>().GetPermissionsAsync(alias.SiteId, entityName, permissionName);
                return permissions.ToList();
            });

        private static Task<int> UserIdAsync(WebApplication app, string username) =>
            InTenantAsync(app, async (sp, alias) =>
            {
                var user = await sp.GetRequiredService<IUserRepository>().GetUserAsync(username);
                Assert.NotNull(user);
                return user.UserId;
            });

        /// <summary>Creates a REGISTERED user and adds the Administrators role - a site admin who is NOT the host.</summary>
        private Task CreateSiteAdminAsync(WebApplication app) =>
            InTenantAsync(app, async (sp, alias) =>
            {
                var created = await sp.GetRequiredService<IUserManager>().AddUser(new User
                {
                    Username = SiteAdminUsername,
                    Password = SiteAdminPassword,
                    Email = SiteAdminEmail,
                    DisplayName = SiteAdminUsername,
                    SiteId = alias.SiteId,
                    EmailConfirmed = true
                });
                Assert.NotNull(created);

                var roles = await sp.GetRequiredService<IRoleRepository>().GetRolesAsync(alias.SiteId);
                var admin = roles.Single(role => role.Name == RoleNames.Admin);
                await sp.GetRequiredService<IUserRoleRepository>().AddUserRoleAsync(new UserRole { UserId = created.UserId, RoleId = admin.RoleId });

                var reloaded = await sp.GetRequiredService<IUserManager>().GetUserAsync(created.UserId, alias.SiteId);
                _output.WriteLine($"site admin '{SiteAdminUsername}' roles = {reloaded.Roles}");
                Assert.Contains(RoleNames.Admin, reloaded.Roles);
                Assert.DoesNotContain(RoleNames.Host, reloaded.Roles);
                return true;
            });

        private async Task LoginAsync(WebApplication app, HttpClient client, CookieContainer cookies, string username, string password)
        {
            var (requestToken, antiforgeryCookie) = GenerateAntiforgery(app);

            var request = new HttpRequestMessage(HttpMethod.Post, "/pages/login")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = username,
                    ["password"] = password,
                    ["remember"] = "false",
                    ["returnurl"] = "/",
                    ["__RequestVerificationToken"] = requestToken
                })
            };
            request.Headers.TryAddWithoutValidation("Cookie", antiforgeryCookie);

            var response = await client.SendAsync(request);
            _output.WriteLine($"[login {username}] POST /pages/login -> {(int)response.StatusCode}");
            CaptureCookies(cookies, new Uri("http://" + TenantHost), response);
        }

        private async Task<HttpStatusCode> PutDefinitionAsync(WebApplication app, HttpClient client, CookieContainer cookies, int userId, ModuleDefinition definition)
        {
            var result = await SendAsync(app, client, cookies, userId, HttpMethod.Put,
                $"/api/ModuleDefinition/{definition.ModuleDefinitionId}", JsonContent.Create(definition));
            return result.Status;
        }

        /// <summary>The Host-only endpoint the framework's own <c>User:Write</c> policy guards.</summary>
        private async Task<HttpStatusCode> DeleteUserAsync(WebApplication app, HttpClient client, CookieContainer cookies, int actorUserId, int victimUserId, int siteId)
        {
            var result = await SendAsync(app, client, cookies, actorUserId, HttpMethod.Delete, $"/api/User/{victimUserId}?siteid={siteId}");
            return result.Status;
        }

        /// <summary>One authenticated request with a matched antiforgery pair - the shape the admin UI sends.</summary>
        private async Task<(HttpStatusCode Status, string Body)> SendAsync(WebApplication app, HttpClient client, CookieContainer cookies,
            int? userId, HttpMethod method, string path, HttpContent? content = null)
        {
            var (requestToken, antiforgeryCookie) = GenerateAntiforgery(app, userId);
            var uri = new Uri(new Uri("http://" + TenantHost), path);

            var request = new HttpRequestMessage(method, uri) { Content = content };
            var cookieHeader = cookies.GetCookieHeader(uri);
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                cookieHeader += "; ";
            }
            cookieHeader += antiforgeryCookie;
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, requestToken);

            var response = await client.SendAsync(request);
            CaptureCookies(cookies, uri, response);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }

        private static (string requestToken, string cookie) GenerateAntiforgery(WebApplication app, int? userId = null)
        {
            using var scope = app.Services.CreateScope();
            var antiforgery = scope.ServiceProvider.GetRequiredService<IAntiforgery>();
            var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            if (userId.HasValue)
            {
                var identity = new System.Security.Claims.ClaimsIdentity(Constants.AuthenticationScheme);
                identity.AddClaim(new System.Security.Claims.Claim(
                    System.Security.Claims.ClaimTypes.NameIdentifier, userId.Value.ToString()));
                httpContext.User = new System.Security.Claims.ClaimsPrincipal(identity);
            }
            var tokens = antiforgery.GetAndStoreTokens(httpContext);
            var setCookie = httpContext.Response.Headers.SetCookie.ToString();
            return (tokens.RequestToken, setCookie.Split(';')[0]);
        }

        private void AssertIdentityCookie(CookieContainer cookies)
        {
            var names = cookies.GetCookies(new Uri("http://" + TenantHost)).Cast<Cookie>().Select(c => c.Name).ToList();
            _output.WriteLine($"cookies after login: {string.Join(", ", names)}");
            Assert.True(names.Any(name => name.Contains("Identity.Application")), "the Identity cookie was not issued - authentication failed.");
        }

        private static void CaptureCookies(CookieContainer cookies, Uri uri, HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                return;
            }

            foreach (var setCookie in setCookies)
            {
                var pair = setCookie.Split(';')[0];
                var eq = pair.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }

                try
                {
                    cookies.Add(new Cookie(pair.Substring(0, eq).Trim(), pair.Substring(eq + 1).Trim(), "/", uri.Host));
                }
                catch
                {
                    // ignore malformed/deletion cookies
                }
            }
        }

        private static async Task SafeShutdownAsync(WebApplication app)
        {
            try { await app.StopAsync(new CancellationTokenSource(ShutdownTimeout).Token); } catch { /* best effort */ }
            try { await app.DisposeAsync(); } catch { /* best effort */ }
        }

        private sealed class ScratchInstall : IDisposable
        {
            private readonly string _previousOverridePath;

            public ScratchInstall()
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-defwrite-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path.Combine(ContentRoot, "wwwroot"));
                System.IO.File.Copy(
                    Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
                    Path.Combine(ContentRoot, "appsettings.json"));

                var overrides = new JsonObject
                {
                    ["Database"] = new JsonObject { ["DefaultDBType"] = "Stomme.Database.Sqlite.SqliteDatabase, Stomme.Server" },
                    ["ConnectionStrings"] = new JsonObject
                    {
                        ["DefaultConnection"] = ScratchSqlite.ConnectionString(Path.Combine(ContentRoot, "tenant.db"))
                    },
                    ["Installation"] = new JsonObject
                    {
                        ["DefaultAlias"] = TenantHost,
                        ["HostUsername"] = HostUsername,
                        ["HostPassword"] = HostPassword,
                        ["HostEmail"] = HostEmail,
                        ["HostName"] = "Host"
                    }
                };
                System.IO.File.WriteAllText(
                    Path.Combine(ContentRoot, "appsettings.override.json"),
                    overrides.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                _previousOverridePath = Environment.GetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable);
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, ContentRoot);
            }

            public string ContentRoot { get; }

            public void Dispose()
            {
                Environment.SetEnvironmentVariable(Constants.AppSettingsOverrideEnvironmentVariable, _previousOverridePath);

                // Only this install's pools - never the process-wide ClearAllPools, which closes the pooled
                // connections of every OTHER suite in the run and, hammered while a host served requests,
                // reproduced empty 500s (3 in 60, measured). This used to name the tenant store alone, which is
                // the hard-coded-list shape that leaks: an installed host also opens the FeatureManagement and
                // module-aggregate sidecars, so the tree survived anyway. ReleaseInstall enumerates.
                ScratchSqlite.ReleaseInstall(ContentRoot);

                try { Directory.Delete(ContentRoot, true); } catch { /* best effort */ }
            }
        }
    }
}