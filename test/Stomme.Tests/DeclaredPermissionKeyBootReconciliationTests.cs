#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The BOOT PASS alone re-seeds a declared key whose rows are missing — no page load required.
    /// </summary>
    /// <remarks>
    /// <b>This sits alongside <see cref="PermissionKeyUpgradeReconciliationTests"/>, it does not replace it.</b>
    /// That suite drives the full two-release upgrade — a key a newer release declares appears on an
    /// already-installed site, and an operator''s grant survives — and it reconciles by READING the definitions
    /// itself, which is exactly the laziness this test guards against. This one removes that read: it asserts the boot
    /// pass on its own is enough, which is what makes the other suite''s "the boot/read path is what reconciles"
    /// literally true rather than true-because-the-test-did-it.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// <c>ReconcileDeclaredKeyPermissionsAsync</c> — which seeds a declared key's rows for a site — runs inside
    /// <c>ProcessModuleDefinitionsAsync</c>, and that is reached only on a MISS of a per-alias cache held at
    /// <c>TimeSpan.MaxValue</c>. So on an existing site a key shipped by a newer release has no rows until
    /// something reads that site's definitions, and the authorization bridge DENIES a key with no rows — to
    /// administrators as well. The feature is simply invisible, with no error to explain it.
    /// </para>
    /// <para>
    /// <b>How the upgrade is simulated, and why that is faithful.</b> A real upgrade is a new PROCESS, whose
    /// in-memory cache is empty, so the first per-site read reconciles. A test cannot restart the host, so it
    /// removes the cached entry for the alias — which is precisely the state a restart produces — and then
    /// deletes the key's rows to recreate "a key the newer release declares and this site has never had".
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class DeclaredPermissionKeyBootReconciliationTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public DeclaredPermissionKeyBootReconciliationTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task A_declared_key_whose_rows_are_missing_is_re_seeded_by_the_boot_pass()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            var app = await GateDrive.InstallAsync(scratch, _output, _ => { }, start: false);

            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                var alias = await GateDrive.AliasAsync(app, Host);
                var siteId = alias.SiteId;

                // ---- find a key that HAS rows, so the test is about losing them, not about a key nothing declares
                var before = await KeyRowsAsync(app, siteId);
                Assert.NotEmpty(before);

                var key = before
                    .GroupBy(row => (row.EntityName, row.PermissionName))
                    .First(group => group.Any(row => row.IsAuthorized));

                var entityName = key.Key.EntityName;
                var permissionName = key.Key.PermissionName;
                _output.WriteLine($"[key] {entityName}:{permissionName} has {key.Count()} row(s)");

                // ---- recreate "a key the newer release declares and this site has never had" ------------------
                await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var permissions = sp.GetRequiredService<IPermissionRepository>();
                    foreach (var row in key)
                    {
                        await permissions.DeletePermissionAsync(row.PermissionId);
                    }

                    return true;
                });

                Assert.Empty(await RowsForAsync(app, siteId, entityName, permissionName));
                _output.WriteLine($"[deleted] {entityName}:{permissionName} now has no rows - the bridge would DENY it, administrators included");

                // A restart empties this cache; a test cannot restart, so it removes the entry instead. Without
                // this the boot pass reads a HIT and reconciles nothing, which is the real-world behaviour inside
                // one process and exactly why the defect survives a page load that already happened.
                await GateDrive.InTenantAsync(app, Host, async (sp, current) =>
                {
                    await sp.GetRequiredService<ICacheManager>().RemoveCacheAsync(current, "ModuleDefinitions");
                    return true;
                });

                // ---- the BOOT pass, exactly as DatabaseManager runs it ---------------------------------------
                await RunBootPassAsync(app);

                var after = await RowsForAsync(app, siteId, entityName, permissionName);
                _output.WriteLine($"[after the boot pass] {after.Count} row(s): " +
                                  string.Join(", ", after.Select(row => $"{row.RoleName ?? ("user " + row.UserId)}={row.IsAuthorized}")));

                Assert.NotEmpty(after);
                Assert.Contains(after, row => row.IsAuthorized);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>Runs the shared reconciler runner the way <c>DatabaseManager</c> does on the boot path.</summary>
        private static Task RunBootPassAsync(WebApplication app) =>
            new SiteContentReconciliationRunner(
                app.Services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger.Instance).RunAsync();

        /// <summary>
        /// The declared-key rows for one RESOURCE. A key is stored as
        /// <c>(EntityName = resource, EntityId = PermissionKeys.KeyEntityId, PermissionName = operation)</c> -
        /// the resource, NOT "ModuleDefinition", which is what the first version of this test looked under and
        /// why it found nothing.
        /// </summary>
        private static Task<List<Permission>> KeyRowsAsync(WebApplication app, int siteId) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                (await sp.GetRequiredService<IPermissionRepository>().GetPermissionsAsync(siteId, global::Stomme.Health.HealthPermissions.Resource))
                    .Where(row => row.EntityId == PermissionKeys.KeyEntityId)
                    .ToList());

        private static Task<List<Permission>> RowsForAsync(WebApplication app, int siteId, string entityName, string permissionName) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                (await sp.GetRequiredService<IPermissionRepository>().GetPermissionsAsync(siteId, entityName, permissionName))
                    .Where(row => row.EntityId == PermissionKeys.KeyEntityId)
                    .ToList());
    }
}

