#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-142: the backfill that takes the <c>Everyone</c> View grant off user folders BL-115 could not reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// BL-115 closed the two places that SEED a user's "My Folder", so folders created since are private — and
    /// did nothing for the ones that already existed, including the host account's, created by the installer.
    /// Those still grant View to <see cref="RoleNames.Everyone"/>, which <c>UserSecurity.IsAuthorized</c> treats
    /// as granted to an ANONYMOUS caller, so every file under <c>Users/{id}/</c> remains downloadable by anyone
    /// who guesses a sequential file id. A seed fix without a backfill only protects installs that do not exist
    /// yet.
    /// </para>
    /// <para>
    /// The exposure is PLANTED here rather than waited for: the current seed is already private (that is BL-115,
    /// pinned by <c>UserFolderPrivacyTests</c>), so the only way to test the repair of an OLD install is to
    /// recreate what an old install looks like.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class UserFolderPrivacyBackfillTests
    {
        private const string Host = "localhost";

        private readonly ITestOutputHelper _output;

        public UserFolderPrivacyBackfillTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task The_boot_pass_makes_a_legacy_user_folder_private_and_leaves_a_public_folder_alone()
        {
            using var scratch = new ScratchGateInstall(new SqliteGateStore(), Host);
            var app = await GateDrive.InstallAsync(scratch, _output, _ => { }, start: false);

            try
            {
                await GateDrive.StartNonJobHostedServicesAsync(app);
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;

                // A REAL user: the Permission rows carry a UserId foreign key, so an invented id is a constraint
                // violation rather than a test.
                var userId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                _output.WriteLine($"[host user] {userId}");

                // ---- plant what a PRE-BL-115 install looks like ---------------------------------------------
                var (legacyUserFolder, publicFolder) = await GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                {
                    var folders = sp.GetRequiredService<IFolderRepository>();
                    var root = (await folders.GetFoldersAsync(siteId)).First(folder => folder.Path == "Users/");

                    // Plant the exposure on the REAL user folder wherever possible: the installer already made
                    // one for the host account, and a unique (SiteId, Path) index means a second is impossible -
                    // which is itself the point, since this is the folder a live install actually has.
                    var user = await folders.GetFolderAsync(siteId, $"Users/{userId}/")
                        ?? await folders.AddFolderAsync(new Folder
                        {
                            SiteId = siteId,
                            ParentId = root.FolderId,
                            Name = "My Folder",
                            Type = FolderTypes.Private,
                            Path = $"Users/{userId}/",
                            Order = 1,
                            ImageSizes = "",
                            Capacity = Constants.UserFolderCapacity,
                            IsSystem = true,
                            PermissionList = new List<Permission>()
                        });

                    // The PRE-BL-115 ACL: owner + Administrators AND Everyone - the row that exposes it.
                    await sp.GetRequiredService<IPermissionRepository>().UpdatePermissionsAsync(
                        siteId, EntityNames.Folder, user.FolderId,
                        new List<Permission>
                        {
                            new Permission(PermissionNames.Browse, userId, true),
                            new Permission(PermissionNames.View, userId, true),
                            new Permission(PermissionNames.View, RoleNames.Admin, true),
                            new Permission(PermissionNames.View, RoleNames.Everyone, true),
                            new Permission(PermissionNames.Edit, userId, true)
                        });

                    // A folder an operator MEANT to be public. It is not a user folder, and must survive.
                    var shared = await folders.AddFolderAsync(new Folder
                    {
                        SiteId = siteId,
                        ParentId = root.ParentId,
                        Name = "Brochures",
                        Type = FolderTypes.Public,
                        Path = "Brochures/",
                        Order = 2,
                        ImageSizes = "",
                        Capacity = 0,
                        IsSystem = false,
                        PermissionList = new List<Permission>
                        {
                            new Permission(PermissionNames.View, RoleNames.Everyone, true),
                            new Permission(PermissionNames.Edit, RoleNames.Admin, true)
                        }
                    });

                    return (user, shared);
                });

                Assert.True(await GrantsEveryoneViewAsync(app, siteId, legacyUserFolder.FolderId),
                    "the planted user folder does not grant Everyone View, so this test would prove nothing");
                _output.WriteLine($"[planted] user folder {legacyUserFolder.FolderId} exposed; public folder {publicFolder.FolderId} public");

                // ---- the BOOT pass, exactly as DatabaseManager runs it ---------------------------------------
                await RunBootPassAsync(app);

                // ---- the user folder is private, and every other row survived --------------------------------
                var after = await RowsAsync(app, siteId, legacyUserFolder.FolderId);
                _output.WriteLine("[after] " + string.Join(", ", after.Select(Describe)));

                Assert.DoesNotContain(after, row =>
                    row.PermissionName == PermissionNames.View && row.RoleName == RoleNames.Everyone && row.IsAuthorized);

                // The owner and the administrators keep theirs - the repair removes ONE row, not the folder's ACL.
                Assert.Contains(after, row => row.PermissionName == PermissionNames.View && row.UserId == userId && row.IsAuthorized);
                Assert.Contains(after, row => row.PermissionName == PermissionNames.View && row.RoleName == RoleNames.Admin && row.IsAuthorized);
                Assert.Contains(after, row => row.PermissionName == PermissionNames.Browse && row.UserId == userId && row.IsAuthorized);
                Assert.Contains(after, row => row.PermissionName == PermissionNames.Edit && row.UserId == userId && row.IsAuthorized);

                // ---- and the deliberately public folder is untouched ------------------------------------------
                Assert.True(await GrantsEveryoneViewAsync(app, siteId, publicFolder.FolderId),
                    "the backfill removed Everyone from a folder that is not a user folder");

                // ---- a SECOND pass is a no-op ----------------------------------------------------------------
                await RunBootPassAsync(app);
                var afterSecond = await RowsAsync(app, siteId, legacyUserFolder.FolderId);
                _output.WriteLine($"[second pass] {after.Count} row(s) before, {afterSecond.Count} after");

                Assert.Equal(after.Count, afterSecond.Count);
                Assert.True(await GrantsEveryoneViewAsync(app, siteId, publicFolder.FolderId));
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

        private static Task<List<Permission>> RowsAsync(WebApplication app, int siteId, int folderId) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                (await sp.GetRequiredService<IPermissionRepository>()
                    .GetPermissionsAsync(siteId, EntityNames.Folder, folderId)).ToList());

        private static async Task<bool> GrantsEveryoneViewAsync(WebApplication app, int siteId, int folderId) =>
            (await RowsAsync(app, siteId, folderId))
                .Any(row => row.PermissionName == PermissionNames.View && row.RoleName == RoleNames.Everyone && row.IsAuthorized);

        private static string Describe(Permission row) =>
            $"{row.PermissionName}:{row.RoleName ?? ("user " + row.UserId)}={row.IsAuthorized}";
    }
}
