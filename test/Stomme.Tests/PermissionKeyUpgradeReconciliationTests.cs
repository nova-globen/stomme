using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Server;
using Stomme.Shared;
using Stomme.Tests.Fixtures.GateTestModule;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// A permission KEY added by a NEWER release must reach an ALREADY-INSTALLED site - and must not disturb
    /// what the operator has granted on the keys that were already there (F7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect class this pins.</b> The per-site materialisation of a module definition's permissions runs
    /// only when that definition has NO rows on the site yet (<c>ModuleDefinitionRepository</c>: "permissions for
    /// module definition do not exist for this site"). Ship a release that adds a key and every existing site is
    /// missing its row forever - and because the guard DENIES when a key has no rows (the no-fallback overload
    /// never admits by role membership), the new capability is dead for everyone, administrators included. This is the admin-page
    /// upgrade class exactly (<see cref="AdminPageUpgradeReconciliationTests"/>), which is why it is driven the
    /// same way: TWO hosts over ONE database, never a fresh install.
    /// </para>
    /// <para>
    /// <b>The second case keeps the fix honest.</b> Reconciliation must be purely ADDITIVE: it adds only the
    /// rows of a key that has none, and never re-processes a key that already has rows - so an operator's grant
    /// to a custom role survives every boot. A reconciliation that replayed the declaration would silently
    /// revert it, which is the exact trap CreatePagesAsync sprang on the admin-page fix.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class PermissionKeyUpgradeReconciliationTests
    {
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

        private const string TenantHost = "localhost";
        private const string HostUsername = "host";
        private const string HostPassword = "H0st!Keys$2026";
        private const string HostEmail = "host@keys.test";

        private const string GateRole = "GateRole";

        private readonly ITestOutputHelper _output;

        public PermissionKeyUpgradeReconciliationTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_key_from_a_newer_release_is_added_to_an_already_installed_site_without_touching_existing_grants()
        {
            using var scratch = new ScratchInstall();
            var declaredBefore = ModuleInfo.DeclaredKeys;

            try
            {
                // ---- Phase 1: install on a release that declares ONLY 'gatetest:manage' ------------------
                ModuleInfo.DeclaredKeys = new[] { GateTestPermissions.Manage };
                var older = await BuildAsync(scratch);
                int siteId;
                try
                {
                    await InstallAsync(older);
                    siteId = await SiteIdAsync(older);

                    // The declared key materialised on the fresh site; the key the older release never declared did not.
                    var manage = await RowsAsync(older, "gatetest", "manage");
                    _output.WriteLine($"phase 1: gatetest:manage rows = {Describe(manage)}");
                    Assert.Contains(manage, row => row.RoleName == RoleNames.Admin && row.IsAuthorized);
                    Assert.All(manage, row => Assert.Equal(PermissionKeys.KeyEntityId, row.EntityId));
                    Assert.Empty(await RowsAsync(older, "gatetest", "view"));

                    // The operator grants the key to a custom role - the thing an upgrade must not revert.
                    await GrantToCustomRoleAsync(older, siteId, "gatetest", "manage");
                    var granted = await RowsAsync(older, "gatetest", "manage");
                    _output.WriteLine($"phase 1: after the operator grant = {Describe(granted)}");
                    Assert.Contains(granted, row => row.RoleName == GateRole && row.IsAuthorized);
                }
                finally
                {
                    await SafeShutdownAsync(older);
                }

                // ---- Phase 2: same database, a newer release that also declares 'gatetest:view' ----------
                ModuleInfo.DeclaredKeys = GateTestPermissions.All;
                var newer = await BuildAsync(scratch);
                try
                {
                    // The boot/read path is what reconciles - exactly as a real upgrade boot does.
                    var definitions = await DefinitionsAsync(newer);

                    var view = await RowsAsync(newer, "gatetest", "view");
                    _output.WriteLine($"phase 2: gatetest:view rows = {Describe(view)}");
                    Assert.Contains(view, row => row.RoleName == RoleNames.Admin && row.IsAuthorized);
                    Assert.All(view, row => Assert.Equal(PermissionKeys.KeyEntityId, row.EntityId));

                    // Additive: the operator's grant survived, and the seeded Administrators row was not duplicated.
                    var manage = await RowsAsync(newer, "gatetest", "manage");
                    _output.WriteLine($"phase 2: gatetest:manage rows = {Describe(manage)}");
                    Assert.Contains(manage, row => row.RoleName == GateRole && row.IsAuthorized);
                    Assert.Single(manage, row => row.RoleName == RoleNames.Admin);

                    // F9: the definition READ path merges the foreign EntityId = -1 rows back into the
                    // definition's PermissionList, so the admin grid renders them instead of an empty cell.
                    var definition = definitions.Single(item => item.Name == "GateTest");
                    _output.WriteLine($"phase 2: GateTest PermissionList = {Describe(definition.PermissionList)}");
                    Assert.Contains(definition.PermissionList, row => row.EntityName == "gatetest" && row.PermissionName == "manage" && row.RoleName == GateRole);
                    Assert.Contains(definition.PermissionList, row => row.EntityName == "gatetest" && row.PermissionName == "view" && row.RoleName == RoleNames.Admin);
                    Assert.Contains(definition.PermissionList, row => row.EntityName == EntityNames.ModuleDefinition && row.PermissionName == PermissionNames.Utilize);

                    // Idempotence: a second boot-path read adds nothing and duplicates nothing.
                    await ClearDefinitionCacheAsync(newer);
                    await DefinitionsAsync(newer);
                    var manageAgain = await RowsAsync(newer, "gatetest", "manage");
                    var viewAgain = await RowsAsync(newer, "gatetest", "view");
                    _output.WriteLine($"phase 2 (second read): manage = {Describe(manageAgain)}; view = {Describe(viewAgain)}");
                    Assert.Equal(manage.Count, manageAgain.Count);
                    Assert.Equal(view.Count, viewAgain.Count);
                }
                finally
                {
                    await SafeShutdownAsync(newer);
                }
            }
            finally
            {
                ModuleInfo.DeclaredKeys = declaredBefore;
            }
        }

        // ---- helpers ---------------------------------------------------------------------------------------

        private static Task<WebApplication> BuildAsync(ScratchInstall scratch) =>
            Program.BuildAppAsync(
                new[] { "--contentRoot", scratch.ContentRoot, "--environment", "Development" },
                builder => builder.WebHost.UseTestServer());

        private static string Describe(IEnumerable<Permission> rows) =>
            "[" + string.Join(", ", rows.Select(row => $"{row.EntityName}:{row.PermissionName}:{row.RoleName ?? ("user " + row.UserId)}={row.IsAuthorized}(entityId {row.EntityId})")) + "]";

        private static async Task<T> InTenantAsync<T>(WebApplication app, Func<IServiceProvider, Alias, Task<T>> work)
        {
            using var scope = app.Services.CreateScope();
            var sp = scope.ServiceProvider;
            var alias = await sp.GetRequiredService<IAliasRepository>().GetAliasAsync(TenantHost)
                ?? throw new InvalidOperationException($"No alias for '{TenantHost}'.");
            await sp.GetRequiredService<ITenantManager>().SetAliasAsync(alias);
            return await work(sp, alias);
        }

        private static Task<int> SiteIdAsync(WebApplication app) =>
            InTenantAsync(app, (sp, alias) => Task.FromResult(alias.SiteId));

        private static Task<List<Permission>> RowsAsync(WebApplication app, string resource, string operation) =>
            InTenantAsync(app, async (sp, alias) =>
            {
                var permissions = await sp.GetRequiredService<IPermissionRepository>()
                    .GetPermissionsAsync(alias.SiteId, resource, operation);
                return permissions.ToList();
            });

        private static Task<List<ModuleDefinition>> DefinitionsAsync(WebApplication app) =>
            InTenantAsync(app, async (sp, alias) =>
            {
                var definitions = await sp.GetRequiredService<IModuleDefinitionRepository>()
                    .GetModuleDefinitionsAsync(alias.SiteId);
                return definitions.ToList();
            });

        private static Task ClearDefinitionCacheAsync(WebApplication app) =>
            InTenantAsync(app, async (sp, alias) =>
            {
                await sp.GetRequiredService<ICacheManager>().RemoveCacheAsync(alias, "ModuleDefinitions");
                return true;
            });

        /// <summary>
        /// Grants the key to a NEW custom role, through the very path the admin grid saves by - so what the
        /// upgrade must preserve is a genuine operator grant, not a hand-inserted row.
        /// </summary>
        private static Task GrantToCustomRoleAsync(WebApplication app, int siteId, string resource, string operation) =>
            InTenantAsync(app, async (sp, alias) =>
            {
                var roles = sp.GetRequiredService<IRoleRepository>();
                var role = (await roles.GetRolesAsync(siteId)).FirstOrDefault(item => item.Name == GateRole)
                    ?? await roles.AddRoleAsync(new Role { SiteId = siteId, Name = GateRole, Description = "BL-99 operator role", IsAutoAssigned = false, IsSystem = false });

                var permissions = sp.GetRequiredService<IPermissionRepository>();
                var submitted = (await permissions.GetPermissionsAsync(siteId, resource, operation)).ToList();
                submitted.Add(new Permission(siteId, resource, PermissionKeys.KeyEntityId, operation, role.Name, null, true));
                await permissions.UpdatePermissionsAsync(siteId, resource, PermissionKeys.KeyEntityId, submitted);
                return true;
            });

        private async Task InstallAsync(WebApplication app)
        {
            var databaseManager = app.Services.GetRequiredService<IDatabaseManager>();
            var installTask = Task.Run(() => databaseManager.InstallAsync());
            Assert.True(await Task.WhenAny(installTask, Task.Delay(InstallTimeout)) == installTask, "Install timed out.");
            var install = await installTask;
            Assert.True(install.Success, $"Install failed: {install.Message}");
        }

        private static async Task SafeShutdownAsync(WebApplication app)
        {
            try { await app.StopAsync(new CancellationTokenSource(ShutdownTimeout).Token); } catch { /* best effort */ }
            try { await app.DisposeAsync(); } catch { /* best effort */ }
        }

        /// <summary>A throwaway SQLite install two hosts can be built over in turn - the shape of an upgrade.</summary>
        private sealed class ScratchInstall : IDisposable
        {
            private readonly string _previousOverridePath;

            public ScratchInstall()
            {
                ContentRoot = Path.Combine(Path.GetTempPath(), "stomme-keyupgrade-" + Guid.NewGuid().ToString("N"));
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