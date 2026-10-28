using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-115: a user's "My Folder" is PRIVATE. It used to be seeded with a View row for
    /// <see cref="RoleNames.Everyone"/>, which an ANONYMOUS caller matches
    /// (<c>UserSecurity.IsAuthorized</c> treats Everyone as granted to everybody), so every file under
    /// <c>Users/{id}/</c> was readable by anyone who guessed the sequential file id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Driven, not read: a real file is planted in a real user's folder through the repositories and the
    /// physical path the controller itself resolves, then fetched over HTTP on every route that serves file
    /// BYTES - <c>api/File/download/{id}</c>, <c>api/File/download/{id}/attach</c>, <c>/files/id/{id}</c> and
    /// <c>/files/{path}</c> - plus the folder listing <c>api/File?folder={id}</c>.
    /// </para>
    /// <para>
    /// A refusal is NOT a bodiless response: <c>DownloadInline</c> answers 403 with error.png BYTES and the
    /// Files page answers 403 with a broken-file image, so "denied" is asserted as "not 200, and the planted
    /// sentinel is absent", never as "the body is empty".
    /// </para>
    /// <para>
    /// Both directions: the OWNER and a site ADMINISTRATOR still get 200 and the sentinel, so the fix is not
    /// simply "nobody can read it".
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class UserFolderPrivacyTests
    {
        private const string Host = EntityPermissionWriteValidationTests.Host;
        private const string OwnerPassword = "Own3r!Folder$2026";
        private const string OtherPassword = "0ther!Folder$2026";
        private const string AdminPassword = "Adm1n!Folder$2026";

        private readonly ITestOutputHelper _output;

        public UserFolderPrivacyTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public Task A_users_my_folder_is_not_readable_by_anonymous_or_other_users() =>
            RunAsync(new SqliteGateStore(), _output);

        internal static async Task RunAsync(IGateStore store, ITestOutputHelper output)
        {
            Assert.True(store.IsReachable(), store.UnreachableMessage);

            using var scratch = await GateInstallTemplates.CloneAsync(store, Host, output);
            var app = await GateDrive.InstallAsync(scratch, output);

            try
            {
                var siteId = (await GateDrive.AliasAsync(app, Host)).SiteId;
                var owner = await GateDrive.AddUserAsync(app, Host, "bl115owner", OwnerPassword, "bl115owner@gate.test");
                var other = await GateDrive.AddUserAsync(app, Host, "bl115other", OtherPassword, "bl115other@gate.test");
                var admin = await GateDrive.AddUserAsync(app, Host, "bl115admin", AdminPassword, "bl115admin@gate.test", RoleNames.Admin);

                // ---- the seeded rows: owner + Administrators, and NO Everyone ------------------------------
                var folder = await GateDrive.InTenantAsync(app, Host, (sp, alias) =>
                    sp.GetRequiredService<IFolderRepository>().GetFolderAsync(alias.SiteId, $"Users/{owner.UserId}/"));
                Assert.True(folder != null, $"No My Folder was seeded for user {owner.UserId}.");
                var rows = folder!.PermissionList ?? new List<Permission>();
                output.WriteLine($"[{store.Name}] Users/{owner.UserId}/ rows = {GateDrive.Describe(rows)}");

                var failures = new List<string>();
                var viewRows = rows.Where(row => row.PermissionName == PermissionNames.View && row.IsAuthorized).ToList();
                if (viewRows.Any(row => row.RoleName == RoleNames.Everyone))
                {
                    failures.Add($"the folder still grants View to {RoleNames.Everyone}");
                }

                if (!viewRows.Any(row => row.UserId == owner.UserId))
                {
                    failures.Add("the OWNER has no View row");
                }

                if (!viewRows.Any(row => row.RoleName == RoleNames.Admin))
                {
                    failures.Add($"{RoleNames.Admin} has no View row");
                }

                // ---- plant a real file with a sentinel, through the repositories ---------------------------
                var sentinel = "BL115-SECRET-" + Guid.NewGuid().ToString("N");
                var planted = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                {
                    var folders = sp.GetRequiredService<IFolderRepository>();
                    var files = sp.GetRequiredService<IFileRepository>();
                    var folderPath = await folders.GetFolderPathAsync(folder!);
                    Directory.CreateDirectory(folderPath);
                    var name = "bl115-private.txt";
                    await System.IO.File.WriteAllTextAsync(Path.Combine(folderPath, name), sentinel);
                    return await files.AddFileAsync(new Models.File
                    {
                        Name = name,
                        FolderId = folder!.FolderId,
                        Extension = "txt",
                        Size = sentinel.Length,
                        ImageHeight = 0,
                        ImageWidth = 0
                    });
                });
                output.WriteLine($"[{store.Name}] planted file {planted.FileId} '{planted.Name}' in folder {folder!.FolderId}");

                var routes = new[]
                {
                    $"/api/File/download/{planted.FileId}",
                    $"/api/File/download/{planted.FileId}/attach",
                    $"/files/id/{planted.FileId}",
                    $"/files/users/{owner.UserId}/{planted.Name}",
                    $"/api/File?folder={folder!.FolderId}"
                };

                // ---- denied: anonymous, and a DIFFERENT registered user ------------------------------------
                using var anonymous = GateDrive.NewClient(app, Host);
                using var otherClient = GateDrive.NewClient(app, Host);
                var otherCookies = await GateDrive.LoginAsync(app, otherClient, Host, "bl115other", OtherPassword, output);

                foreach (var route in routes)
                {
                    var anonymousGet = await GateDrive.GetAsync(anonymous, null, Host, route, output, $"{store.Name} anonymous {route}");
                    if (anonymousGet.Body.Contains(sentinel) || (anonymousGet.Status == HttpStatusCode.OK && route.Contains("/api/File/download")))
                    {
                        failures.Add($"ANONYMOUS {route} -> {(int)anonymousGet.Status}{(anonymousGet.Body.Contains(sentinel) ? " WITH THE FILE CONTENT" : "")}");
                    }

                    var otherGet = await GateDrive.GetAsync(otherClient, otherCookies, Host, route, output, $"{store.Name} other user {route}");
                    if (otherGet.Body.Contains(sentinel))
                    {
                        failures.Add($"another user's {route} -> {(int)otherGet.Status} WITH THE FILE CONTENT");
                    }
                }

                // ---- allowed: the owner, and a site administrator ------------------------------------------
                foreach (var (username, password, who) in new[] { ("bl115owner", OwnerPassword, "owner"), ("bl115admin", AdminPassword, "administrator") })
                {
                    using var client = GateDrive.NewClient(app, Host);
                    var cookies = await GateDrive.LoginAsync(app, client, Host, username, password, output);
                    var download = await GateDrive.GetAsync(client, cookies, Host, $"/api/File/download/{planted.FileId}", output, $"{store.Name} {who} download");
                    if (download.Status != HttpStatusCode.OK || !download.Body.Contains(sentinel))
                    {
                        failures.Add($"the {who} could NOT read the file: {(int)download.Status}");
                    }
                }

                foreach (var failure in failures)
                {
                    output.WriteLine($"[{store.Name}] FAILURE: {failure}");
                }

                Assert.True(failures.Count == 0, $"[{store.Name}] {failures.Count} failure(s): " + string.Join(" | ", failures));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
