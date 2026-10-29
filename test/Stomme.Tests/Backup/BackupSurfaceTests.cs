#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Backup;
using Stomme.Models;
using Stomme.Modules.Backup.Application;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// <c>BackupHostOnlySurfaceTests</c> and <c>BackupDisabledSurfaceTests</c> - the backup surface
    /// over REAL HTTP on an installed host: the Host reaches it, an Administrator is refused (403) and so is an anonymous
    /// caller, and NO permission key mentions backup (refutation 8); the Host takes a snapshot (queued - 202, then Available),
    /// downloads the archive, imports it back (verified before it is listed; the wrong passphrase refused and nothing listed)
    /// and deletes it; a restore is checked and STAGED by the worker. With <c>Backup:Enabled</c> off every route answers as
    /// an unknown one, no admin page exists, and a staged restore is NOT applied at boot.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class BackupSurfaceTests(ITestOutputHelper output)
    {
        private const string Host = "localhost";
        private const string Passphrase = "surface passphrase";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        [Fact]
        public async Task Only_the_Host_reaches_the_backup_surface()
        {
            using var scratch = await ScratchAsync(enabled: true);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var hostClient = GateDrive.NewClient(app, Host);
                var hostCookies = await GateDrive.LoginAsync(app, hostClient, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                Assert.Equal(HttpStatusCode.OK, (await GateDrive.GetAsync(hostClient, hostCookies, Host, "/api/Backup", output, "host lists")).Status);

                await GateDrive.AddUserAsync(app, Host, "bl81-admin", "Bl81!Admin$2026", "bl81-admin@gate.test", RoleNames.Admin);
                using var adminClient = GateDrive.NewClient(app, Host);
                var adminCookies = await GateDrive.LoginAsync(app, adminClient, Host, "bl81-admin", "Bl81!Admin$2026", output);
                Assert.Equal(HttpStatusCode.Forbidden, (await GateDrive.GetAsync(adminClient, adminCookies, Host, "/api/Backup", output, "administrator lists")).Status);
                var adminId = await GateDrive.UserIdAsync(app, Host, "bl81-admin");
                var adminCreate = await GateDrive.SendJsonAsync(app, adminClient, adminCookies, Host, adminId, HttpMethod.Post, "/api/Backup", new BackupPassphraseRequest { Passphrase = Passphrase }, output, "administrator takes a snapshot");
                Assert.Equal(HttpStatusCode.Forbidden, adminCreate.Status);

                using var anonymous = GateDrive.NewClient(app, Host);
                Assert.NotEqual(HttpStatusCode.OK, (await GateDrive.GetAsync(anonymous, null, Host, "/api/Backup", output, "anonymous lists")).Status);
                Assert.Empty(await Engine(app).ListSnapshotsAsync());

                // The page is the Host's too.
                var page = await GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IPageRepository>().GetPagesAsync(alias.SiteId)).SingleOrDefault(p => p.Path == "admin/backup"));
                Assert.NotNull(page);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        /// <summary>Two layers, each sufficient on its own: the class attribute, and the service's own check.</summary>
        [Fact]
        public async Task Each_Host_check_refuses_a_non_Host_on_its_own()
        {
            var authorize = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>(typeof(Stomme.Modules.Backup.Controllers.BackupController));
            Assert.Equal(RoleNames.Host, authorize?.Roles);

            using var scratch = await ScratchAsync(enabled: true);
            var app = await GateDrive.InstallAsync(scratch, output, configure: null!, start: false);
            try
            {
                using var scope = app.Services.CreateScope();
                var provider = scope.ServiceProvider;
                var administrator = new SignedIn(RoleNames.Admin, RoleNames.Registered);
                var service = new Stomme.Modules.Backup.Services.ServerBackupService(provider.GetRequiredService<BackupEngine>(), provider.GetRequiredService<BackupWorkQueue>(),
                    provider.GetRequiredService<Stomme.Modules.Backup.Services.BackupOperationLog>(), provider.GetRequiredService<BackupOptions>(), administrator);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync());
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(new BackupPassphraseRequest { Passphrase = Passphrase }));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SchedulesAsync());
                Assert.Empty(await Engine(app).ListSnapshotsAsync());
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private sealed class SignedIn(params string[] roles) : Stomme.Security.Abstractions.ICurrentUser
        {
            public int? UserId => 42;

            public string? UserName => "not-the-host";

            public bool IsAuthenticated => true;

            public IReadOnlySet<string> Roles { get; } = new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase);

            public int SiteId => 1;
        }

        [Fact]
        public void No_permission_key_mentions_backup()
        {
            var keys = typeof(Stomme.Modules.Backup.ModuleInfo).Assembly.GetTypes().Concat(typeof(BackupEngine).Assembly.GetTypes())
                .Where(type => typeof(Stomme.Modules.IModule).IsAssignableFrom(type) && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) is not null)
                .Select(type => ((Stomme.Modules.IModule)Activator.CreateInstance(type)!).ModuleDefinition.PermissionNames ?? string.Empty)
                .ToList();
            Assert.True(keys.Count >= 10, $"only {keys.Count} module definitions were read - the scan is broken.");
            Assert.Empty(keys.Where(MentionsBackup));
            Assert.True(MentionsBackup("View;portability:export;backup:create"));            // the scanner, validated the other way
        }

        [Fact]
        public async Task The_Host_takes_downloads_imports_and_deletes_a_snapshot_over_HTTP()
        {
            using var scratch = await ScratchAsync(enabled: true);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);

                var refused = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Backup", new BackupPassphraseRequest { Passphrase = "short" }, output, "a short passphrase");
                Assert.Equal(HttpStatusCode.BadRequest, refused.Status);

                var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Backup", new BackupPassphraseRequest { Passphrase = Passphrase }, output, "take a snapshot");
                Assert.Equal(HttpStatusCode.Accepted, created.Status);
                var id = JsonSerializer.Deserialize<BackupOperationResult>(created.Body, Json)!.SnapshotId!.Value;
                var snapshot = await UntilAsync(client, cookies, id, "Available");
                Assert.True(snapshot.SizeBytes > 0);

                var archive = await DownloadAsync(client, cookies, id);
                Assert.Equal(snapshot.SizeBytes, archive.LongLength);
                Assert.Equal("DCBAK1", System.Text.Encoding.ASCII.GetString(archive, 0, 6));

                var wrong = await ImportAsync(app, client, cookies, hostId, archive, "not the passphrase at all");
                Assert.Equal(HttpStatusCode.BadRequest, wrong.Status);
                Assert.Single(await ListAsync(client, cookies));
                Assert.Single(await app.Services.GetRequiredService<Stomme.Modules.Backup.Infrastructure.BackupArchiveStores>().Current.ListAsync());   // the refused upload left no file

                var imported = await ImportAsync(app, client, cookies, hostId, archive, Passphrase);
                Assert.Equal(HttpStatusCode.OK, imported.Status);
                var importedId = JsonSerializer.Deserialize<BackupOperationResult>(imported.Body, Json)!.SnapshotId!.Value;
                var row = (await ListAsync(client, cookies)).Single(view => view.Id == importedId);
                Assert.Equal(("Available", "imported", snapshot.SizeBytes), (row.Status, row.RetentionTag, row.SizeBytes));

                var deleted = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Delete, $"/api/Backup/{importedId}", new { }, output, "delete the import");
                Assert.Equal(HttpStatusCode.NoContent, deleted.Status);
                Assert.Equal(new[] { id }, (await ListAsync(client, cookies)).Select(view => view.Id));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task A_restore_over_HTTP_is_checked_and_staged_by_the_worker()
        {
            using var scratch = await ScratchAsync(enabled: true);
            var app = await GateDrive.InstallAsync(scratch, output);
            try
            {
                using var client = GateDrive.NewClient(app, Host);
                var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Backup", new BackupPassphraseRequest { Passphrase = Passphrase }, output, "take a snapshot");
                var id = JsonSerializer.Deserialize<BackupOperationResult>(created.Body, Json)!.SnapshotId!.Value;
                await UntilAsync(client, cookies, id, "Available");

                var restore = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Backup/{id}/restore", new BackupPassphraseRequest { Passphrase = Passphrase }, output, "stage a restore");
                Assert.Equal(HttpStatusCode.Accepted, restore.Status);

                BackupOperationResult? report = null;
                for (var attempt = 0; attempt < 120 && report is not { Completed: true }; attempt++)
                {
                    await Task.Delay(500);
                    var answer = await GateDrive.GetAsync(client, cookies, Host, $"/api/Backup/{id}/restore", output, "the restore report");
                    report = answer.Status == HttpStatusCode.OK ? JsonSerializer.Deserialize<BackupOperationResult>(answer.Body, Json) : null;
                }

                Assert.True(report is { Completed: true, Accepted: true }, string.Join("; ", report?.Problems ?? new List<string>()));
                Assert.Equal("Staged", (await ListAsync(client, cookies)).Single(view => view.Id == id).Status);
                Assert.True(System.IO.File.Exists(Engine(app).RequestFile));
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        [Fact]
        public async Task With_Backup_disabled_there_is_no_surface_no_page_and_no_boot_phase()
        {
            using var scratch = await ScratchAsync(enabled: true);
            Guid snapshot;
            var first = await GateDrive.InstallAsync(scratch, output, configure: null!, start: false);
            await GateDrive.StartNonJobHostedServicesAsync(first);
            try
            {
                snapshot = await Engine(first).CreateSnapshotAsync(Passphrase, Stomme.Modules.Backup.Domain.RetentionTag.Manual);
                Assert.True((await Engine(first).StageRestoreAsync(snapshot, Passphrase)).Ok);
            }
            finally
            {
                await GateDrive.ShutdownAsync(first);
            }

            // Switched off in the override file, as an operator would.
            var overridePath = Path.Combine(scratch.ContentRoot, "appsettings.override.json");
            var settings = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(overridePath))!;
            settings["Backup"] = new JsonObject { ["Enabled"] = false };
            await System.IO.File.WriteAllTextAsync(overridePath, settings.ToJsonString());

            var second = await GateDrive.InstallAsync(scratch, output);
            try
            {
                Assert.True(System.IO.File.Exists(Engine(second).RequestFile), "the boot phase acted while Backup was disabled.");
                Assert.Equal(Stomme.Modules.Backup.Domain.BackupSnapshotStatus.Staged, (await Engine(second).GetSnapshotAsync(snapshot))!.Status);

                using var client = GateDrive.NewClient(second, Host);
                var cookies = await GateDrive.LoginAsync(second, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, output);
                var unknown = await GateDrive.GetAsync(client, cookies, Host, "/api/NoSuchBackupRoute", output, "an unknown route");
                Assert.Equal(unknown.Status, (await GateDrive.GetAsync(client, cookies, Host, "/api/Backup", output, "the disabled surface")).Status);
                Assert.NotEqual(HttpStatusCode.OK, (await GateDrive.GetAsync(client, cookies, Host, $"/api/Backup/{snapshot}/download", output, "a disabled download")).Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(second);
            }

            // A fresh install with Backup never switched on has no page.
            using var fresh = await ScratchAsync(enabled: false);
            var third = await GateDrive.InstallAsync(fresh, output, configure: null!, start: false);
            try
            {
                var page = await GateDrive.InTenantAsync(third, Host, async (sp, alias) =>
                    (await sp.GetRequiredService<IPageRepository>().GetPagesAsync(alias.SiteId)).SingleOrDefault(p => p.Path == "admin/backup"));
                Assert.Null(page);
            }
            finally
            {
                await GateDrive.ShutdownAsync(third);
            }
        }

        [Fact]
        public async Task Switching_Backup_on_later_adds_the_page_to_an_existing_site_at_the_next_boot()
        {
            using var scratch = await ScratchAsync(enabled: false);
            var before = await GateDrive.InstallAsync(scratch, output, configure: null!, start: false);
            try
            {
                Assert.Null(await BackupPageAsync(before));
            }
            finally
            {
                await GateDrive.ShutdownAsync(before);
            }

            var overridePath = Path.Combine(scratch.ContentRoot, "appsettings.override.json");
            var settings = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(overridePath))!;
            settings["Backup"] = new JsonObject { ["Enabled"] = true };
            await System.IO.File.WriteAllTextAsync(overridePath, settings.ToJsonString());

            var after = await GateDrive.InstallAsync(scratch, output, configure: null!, start: false);
            try
            {
                var page = await BackupPageAsync(after);
                Assert.NotNull(page);
                Assert.Contains(page!.PermissionList, permission => permission.PermissionName == PermissionNames.View && permission.RoleName == RoleNames.Host);
                Assert.DoesNotContain(page.PermissionList, permission => permission.RoleName == RoleNames.Admin);
            }
            finally
            {
                await GateDrive.ShutdownAsync(after);
            }
        }

        private static Task<Page?> BackupPageAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var summary = (await sp.GetRequiredService<IPageRepository>().GetPagesAsync(alias.SiteId)).SingleOrDefault(p => p.Path == "admin/backup");
                return summary is null ? null : await sp.GetRequiredService<IPageRepository>().GetPageAsync(summary.PageId);
            });

        private static bool MentionsBackup(string keys) => keys.Split(';', StringSplitOptions.RemoveEmptyEntries).Any(key => key.StartsWith("backup:", StringComparison.OrdinalIgnoreCase));

        private Task<ScratchGateInstall> ScratchAsync(bool enabled) =>
            GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Backup"] = new JsonObject { ["Enabled"] = enabled } });

        private static BackupEngine Engine(WebApplication app) => app.Services.CreateScope().ServiceProvider.GetRequiredService<BackupEngine>();

        private async Task<List<BackupSnapshotView>> ListAsync(HttpClient client, CookieContainer cookies)
        {
            var answer = await GateDrive.GetAsync(client, cookies, Host, "/api/Backup", output, "list");
            Assert.Equal(HttpStatusCode.OK, answer.Status);
            return JsonSerializer.Deserialize<List<BackupSnapshotView>>(answer.Body, Json)!;
        }

        private async Task<BackupSnapshotView> UntilAsync(HttpClient client, CookieContainer cookies, Guid id, string status)
        {
            for (var attempt = 0; attempt < 120; attempt++)
            {
                var row = (await ListAsync(client, cookies)).SingleOrDefault(view => view.Id == id);
                if (row?.Status == status)
                {
                    return row;
                }

                await Task.Delay(500);
            }

            throw new TimeoutException($"snapshot {id} never became {status}.");
        }

        private static async Task<byte[]> DownloadAsync(HttpClient client, CookieContainer cookies, Guid id)
        {
            var uri = new Uri(new Uri("http://" + Host), $"/api/Backup/{id}/download");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Cookie", cookies.GetCookieHeader(uri));
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsByteArrayAsync();
        }

        private static async Task<(HttpStatusCode Status, string Body)> ImportAsync(WebApplication app, HttpClient client, CookieContainer cookies, int userId, byte[] archive, string passphrase)
        {
            var (token, antiforgery) = GateDrive.Antiforgery(app, userId);
            var uri = new Uri(new Uri("http://" + Host), "/api/Backup/import");
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(archive);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "archive", "archive.dcbak");
            form.Add(new StringContent(passphrase), "passphrase");
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = form };
            request.Headers.TryAddWithoutValidation("Cookie", cookies.GetCookieHeader(uri) + "; " + antiforgery);
            request.Headers.TryAddWithoutValidation(Constants.AntiForgeryTokenHeaderName, token);
            using var response = await client.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsStringAsync());
        }
    }
}
