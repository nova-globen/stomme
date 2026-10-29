using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// A Public-type folder's files are served as their folder's permissions say, at every address. Driven end to
    /// end: a file in a Public folder whose View was Administrators only answered 403 at
    /// <c>/files/...</c> and 200 WITH ITS BYTES at its static path <c>/Content/Tenants/{t}/Sites/{s}/...</c> - Public
    /// folders live under the web root and the static-file middleware served them to anyone. That path is now sent to the
    /// checked route of its own site.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class PublicFolderStaticPathDriveTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";

        [Fact]
        public async Task A_static_path_answers_as_the_folders_permissions_do_and_never_with_a_restricted_files_bytes()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false, extraSettings: null);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                var (tenantId, siteId) = await GateDrive.InTenantAsync<(int, int)>(app, Host, async (sp, alias) =>
                {
                    var folders = sp.GetRequiredService<IFolderRepository>();
                    var files = sp.GetRequiredService<IFileRepository>();
                    async Task PutAsync(Folder folder, string name, string text)
                    {
                        var path = await folders.GetFolderPathAsync(folder);
                        Directory.CreateDirectory(path);
                        await System.IO.File.WriteAllTextAsync(Path.Combine(path, name), text);
                        await files.AddFileAsync(new Stomme.Models.File { FolderId = folder.FolderId, Name = name, Extension = "txt", Size = text.Length, ImageHeight = 0, ImageWidth = 0, Description = string.Empty });
                    }

                    var open = await folders.GetFolderAsync(alias.SiteId, "Public/");
                    await PutAsync(open, "open.txt", "for everyone");
                    var restricted = await folders.AddFolderAsync(new Folder
                    {
                        SiteId = alias.SiteId, ParentId = open.FolderId, Name = "Restricted", Type = FolderTypes.Public, Path = "Public/Restricted/", Order = 1, ImageSizes = "", Capacity = 0, IsSystem = false,
                        PermissionList = new List<Permission> { new(PermissionNames.Browse, RoleNames.Admin, true), new(PermissionNames.View, RoleNames.Admin, true), new(PermissionNames.Edit, RoleNames.Admin, true) },
                    });
                    await PutAsync(restricted, "secret.txt", "administrators only");
                    return (alias.TenantId, alias.SiteId);
                });

                using var visitor = GateDrive.NewClient(app, Host);
                async Task<(HttpStatusCode Status, string Location, string Body)> GetAsync(string path)
                {
                    using var response = await visitor.GetAsync(new Uri(new Uri("http://" + Host), path));
                    var body = await response.Content.ReadAsStringAsync();
                    var location = response.Headers.Location?.OriginalString ?? string.Empty;
                    output.WriteLine($"GET {path} -> {(int)response.StatusCode} {location}");
                    return (response.StatusCode, location, body);
                }

                var content = $"/Content/Tenants/{tenantId}/Sites/{siteId}/Public/";

                // The restricted file: never its bytes; the static path goes to the checked route, which refuses a visitor.
                var secret = await GetAsync(content + "Restricted/secret.txt");
                Assert.DoesNotContain("administrators only", secret.Body, StringComparison.Ordinal);
                Assert.Equal((HttpStatusCode.Redirect, "/files/Public/Restricted/secret.txt"), (secret.Status, secret.Location));
                var checkedRoute = await GetAsync(secret.Location);
                Assert.NotEqual(HttpStatusCode.OK, checkedRoute.Status);
                Assert.DoesNotContain("administrators only", checkedRoute.Body, StringComparison.Ordinal);

                // A file everyone may view is still reached through the same link.
                var open = await GetAsync(content + "open.txt");
                Assert.Equal((HttpStatusCode.Redirect, "/files/Public/open.txt"), (open.Status, open.Location));
                Assert.Equal((HttpStatusCode.OK, "for everyone"), ((await GetAsync(open.Location)).Status, (await GetAsync(open.Location)).Body));

                // Another tenant's or site's path is not this site's.
                Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"/Content/Tenants/{tenantId + 7}/Sites/{siteId}/Public/open.txt")).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await GetAsync($"/Content/Tenants/{tenantId}/Sites/{siteId + 7}/Public/open.txt")).Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }
    }
}
