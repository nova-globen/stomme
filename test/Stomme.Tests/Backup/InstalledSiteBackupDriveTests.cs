#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Backup;
using Stomme.Models.Pipelines;
using Stomme.Modules.Backup.Application;
using Stomme.Modules.Backup.Domain;
using Stomme.Modules.Guestbook.Domain;
using Stomme.Modules.HtmlText.Repository;
using Stomme.Repository;
using Stomme.SharedKernel.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using Stomme.Persistence.Abstractions;
using Stomme.Tenancy.Abstractions;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// BL-81 (D106, gates <c>InstalledSiteBackupDriveTests</c> and <c>InstalledSiteBackupFreshHostDriveTests</c>) - the D30 bar
    /// on SQLite, PostgreSQL, RavenDB AND (BL-173) SQL Server: an installed site with a guestbook entry, the home page's HtmlText content and an
    /// uploaded file; a snapshot taken over HTTP; afterwards another entry, new content, the file deleted; the restore staged
    /// over HTTP and the host RESTARTED - the file is served again, the later entry is gone, the home page renders the old
    /// content, the Host signs in. And the same archive IMPORTED into a second, fresh installation of the same version and
    /// restored there: the fresh host renders the first one's content.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public abstract class InstalledSiteBackupDriveTests
    {
        private const string Host = "localhost";
        private const string Passphrase = "installed site passphrase";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly ITestOutputHelper _output;

        protected InstalledSiteBackupDriveTests(ITestOutputHelper output) => _output = output;

        private protected abstract IGateStore Store();

        // Pipelines composed too (BL-232): an installation's pipelines are part of what a backup holds.
        private Task<ScratchGateInstall> ScratchAsync() =>
            GateInstallTemplates.CloneAsync(Store(), Host, _output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Backup"] = new JsonObject { ["Enabled"] = true }, ["Pipelines"] = new JsonObject { ["Enabled"] = true } });

        [Fact]
        public async Task A_site_restored_after_a_restart_serves_its_file_renders_its_content_and_lets_the_Host_in()
        {
            using var scratch = await ScratchAsync();
            var content = Encoding.UTF8.GetBytes("the file in the snapshot " + Guid.NewGuid());
            int fileId;
            var first = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                await SignAsync(first, "before the snapshot");
                await SetHomeContentAsync(first, "<p>BL81-BEFORE-THE-SNAPSHOT</p>");
                fileId = await UploadAsync(first, "bl81-drive.txt", content);

                using var client = GateDrive.NewClient(first, Host);
                var cookies = await GateDrive.LoginAsync(first, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var hostId = await GateDrive.UserIdAsync(first, Host, GateDrive.HostUsername);
                var snapshot = await TakeAsync(first, client, cookies, hostId);
                await LocalArchiveAsync(scratch, first, snapshot);

                await SignAsync(first, "after the snapshot");
                await SetHomeContentAsync(first, "<p>BL81-AFTER-THE-SNAPSHOT</p>");
                await GateDrive.InTenantAsync(first, Host, async (sp, _) =>
                {
                    var files = sp.GetRequiredService<IFileRepository>();
                    System.IO.File.Delete(await files.GetFilePathAsync(fileId));
                    await files.DeleteFileAsync(fileId);
                    return 0;
                });
                Assert.NotEqual(HttpStatusCode.OK, (await GateDrive.GetAsync(client, cookies, Host, $"/api/File/download/{fileId}", _output, "the deleted file")).Status);

                var restore = await GateDrive.SendJsonAsync(first, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Backup/{snapshot}/restore", new BackupPassphraseRequest { Passphrase = Passphrase }, _output, "stage the restore");
                Assert.Equal(HttpStatusCode.Accepted, restore.Status);
                await UntilStagedAsync(first, snapshot);
            }
            finally
            {
                await GateDrive.ShutdownAsync(first);
            }

            var second = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                using var client = GateDrive.NewClient(second, Host);
                var cookies = await GateDrive.LoginAsync(second, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);

                var file = await DownloadAsync(client, cookies, fileId);
                Assert.Equal(content, file);
                Assert.Equal(new[] { "before the snapshot" }, await EntriesAsync(second));

                var home = await GateDrive.GetAsync(client, cookies, Host, "/", _output, "home after the restore");
                Assert.Equal(HttpStatusCode.OK, home.Status);
                Assert.Contains("BL81-BEFORE-THE-SNAPSHOT", home.Body, StringComparison.Ordinal);
                Assert.DoesNotContain("BL81-AFTER-THE-SNAPSHOT", home.Body, StringComparison.Ordinal);
                Assert.Equal(BackupSnapshotStatus.Available, (await Engine(second).ListSnapshotsAsync()).Single(row => row.RetentionTag == RetentionTag.Manual).Status);
            }
            finally
            {
                await GateDrive.ShutdownAsync(second);
            }
        }

        [Fact]
        public async Task An_archive_imported_into_a_fresh_installation_restores_there_and_renders()
        {
            byte[] archive;
            string definitionId;
            using (var origin = await ScratchAsync())
            {
                var app = await GateDrive.InstallAsync(origin, _output);
                try
                {
                    await SetHomeContentAsync(app, "<p>BL81-FROM-THE-FIRST-INSTALLATION</p>");
                    await SignAsync(app, "written on the first installation");

                    // BL-232: a pipeline, published and run, on the first installation.
                    using var client = GateDrive.NewClient(app, Host);
                    var cookies = await GateDrive.LoginAsync(app, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                    var hostId = await GateDrive.UserIdAsync(app, Host, GateDrive.HostUsername);
                    var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines", new CreatePipelineRequest { Name = "Kept by the backup" }, _output, "create a pipeline");
                    definitionId = JsonNode.Parse(created.Body)!["definitionId"]!.GetValue<string>();
                    await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/publish", new { }, _output, "publish it");
                    var started = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, $"/api/Pipelines/{definitionId}/runs", new StartPipelineRequest { CorrelationId = "before-the-backup" }, _output, "run it");
                    Assert.Equal(HttpStatusCode.OK, started.Status);
                    await UntilRunStoredAsync(client, cookies, definitionId);
                    // BL-233: and a file in the site's pipeline folder, put there through the Files page's API.
                    Assert.Equal(HttpStatusCode.OK, (await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Pipelines/files/folders", new PipelineFileFolderRequest { Path = "inputs" }, _output, "make a folder of files")).Status);
                    Assert.Equal(HttpStatusCode.OK, await UploadPipelineFileAsync(app, client, cookies, hostId, "inputs", "customers.csv", PipelineFileContent));
                    var id = await Engine(app).CreateSnapshotAsync(Passphrase, RetentionTag.Manual);

                    // The archive the fresh installation restores is the FILE in the first one's data directory - what a host's
                    // copy of its data volume carries offsite - and it is the archive the engine serves for download.
                    archive = await System.IO.File.ReadAllBytesAsync(await LocalArchiveAsync(origin, app, id));
                    await using var stream = (await Engine(app).OpenArchiveAsync(id))!;
                    using var copy = new MemoryStream();
                    await stream.CopyToAsync(copy);
                    Assert.Equal(copy.ToArray(), archive);
                }
                finally
                {
                    await GateDrive.ShutdownAsync(app);
                }
            }

            using var fresh = await ScratchAsync();
            var before = await GateDrive.InstallAsync(fresh, _output);
            try
            {
                Assert.Empty(await EntriesAsync(before));
                var imported = await Engine(before).ImportArchiveAsync(new MemoryStream(archive), Passphrase);
                var staged = await Engine(before).StageRestoreAsync(imported, Passphrase);
                Assert.True(staged.Ok, string.Join("; ", staged.Problems));
            }
            finally
            {
                await GateDrive.ShutdownAsync(before);
            }

            var after = await GateDrive.InstallAsync(fresh, _output);
            try
            {
                Assert.Equal(new[] { "written on the first installation" }, await EntriesAsync(after));
                using var client = GateDrive.NewClient(after, Host);
                var cookies = await GateDrive.LoginAsync(after, client, Host, GateDrive.HostUsername, GateDrive.HostPassword, _output);
                var home = await GateDrive.GetAsync(client, cookies, Host, "/", _output, "the fresh host's home");
                Assert.Equal(HttpStatusCode.OK, home.Status);
                Assert.Contains("BL81-FROM-THE-FIRST-INSTALLATION", home.Body, StringComparison.Ordinal);

                // BL-232: the pipeline is the fresh installation's now - published, with its run.
                var pipelines = JsonNode.Parse((await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines", _output, "the fresh host's pipelines")).Body)!.AsArray();
                var pipeline = Assert.Single(pipelines, p => p?["definitionId"]?.GetValue<string>() == definitionId)!;
                Assert.Equal(("Kept by the backup", true), (pipeline["name"]?.GetValue<string>(), pipeline["isPublished"]?.GetValue<bool>()));
                var runs = JsonNode.Parse((await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}", _output, "its runs")).Body)!.AsArray();
                Assert.Equal("before-the-backup", Assert.Single(runs)!["correlationId"]?.GetValue<string>());

                // BL-233: and the file its pipelines read, in the fresh installation's site folder.
                var kept = await GateDrive.GetAsync(client, cookies, Host, "/api/Pipelines/files/content?path=inputs%2Fcustomers.csv", _output, "the restored pipeline file");
                Assert.Equal((HttpStatusCode.OK, PipelineFileContent), (kept.Status, kept.Body));
            }
            finally
            {
                await GateDrive.ShutdownAsync(after);
            }
        }

        private const string PipelineFileContent = "customer,total\nAda,42\n";

        // A file into the site's pipeline folder as the Files page sends it: multipart, under "file", with the antiforgery header.
        private async Task<HttpStatusCode> UploadPipelineFileAsync(WebApplication app, HttpClient client, CookieContainer cookies, int userId, string folder, string name, string content)
        {
            var (requestToken, antiforgeryCookie) = GateDrive.Antiforgery(app, userId);
            var uri = new Uri(new Uri("http://" + Host), "/api/Pipelines/files?folder=" + Uri.EscapeDataString(folder));
            using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", name } };
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = form };
            var cookieHeader = cookies.GetCookieHeader(uri);
            request.Headers.TryAddWithoutValidation("Cookie", string.IsNullOrEmpty(cookieHeader) ? antiforgeryCookie : cookieHeader + "; " + antiforgeryCookie);
            request.Headers.TryAddWithoutValidation(Stomme.Shared.Constants.AntiForgeryTokenHeaderName, requestToken);
            using var response = await client.SendAsync(request);
            _output.WriteLine($"[upload a pipeline file] POST {uri.PathAndQuery} -> {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            return response.StatusCode;
        }

        // A dispatched run is stored once its first burst of work ends; the backup is taken after it is.
        private async Task UntilRunStoredAsync(HttpClient client, CookieContainer cookies, string definitionId)
        {
            for (var attempt = 0; attempt < 120; attempt++)
            {
                var runs = await GateDrive.GetAsync(client, cookies, Host, $"/api/Pipelines/runs?definitionId={definitionId}", _output, "runs so far");
                if (runs.Status == HttpStatusCode.OK && JsonNode.Parse(runs.Body)!.AsArray().Count > 0)
                {
                    return;
                }

                await Task.Delay(250);
            }

            throw new TimeoutException($"the run of {definitionId} was never stored.");
        }

        private static BackupEngine Engine(WebApplication app) => app.Services.CreateScope().ServiceProvider.GetRequiredService<BackupEngine>();

        /// <summary>
        /// S2 item 4 (infra's C15: Local is the default, S3 an option): with NO <c>Backup:Archive</c> in the installation's
        /// settings, a snapshot is recorded in the Local store and its archive is a file in <c>Data/backups</c> under the
        /// host's content root - the data directory a container mounts as its volume. The path is computed from the scratch
        /// install's content root, not asked of the host, so a host that wrote elsewhere fails here. Returns the file's path.
        /// </summary>
        private async Task<string> LocalArchiveAsync(ScratchGateInstall scratch, WebApplication app, Guid snapshot)
        {
            var settings = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(Path.Combine(scratch.ContentRoot, "appsettings.override.json")))!;
            Assert.Null(settings["Backup"]?["Archive"]);

            var row = (await Engine(app).GetSnapshotAsync(snapshot))!;
            Assert.Equal("Local", row.ArchiveStore);
            var dataDirectory = Path.Combine(scratch.ContentRoot, "Data");
            Assert.Equal(Path.GetFullPath(dataDirectory), Path.GetFullPath(app.Services.GetRequiredService<Stomme.Infrastructure.IConfigManager>().GetDataDirectory()));
            var file = Path.Combine(dataDirectory, "backups", row.StorageLocation.Value);
            _output.WriteLine($"[local archive] snapshot {snapshot}: {file}, {row.SizeBytes} bytes");
            Assert.True(System.IO.File.Exists(file), $"no archive at {file}");
            Assert.Equal(row.SizeBytes, new FileInfo(file).Length);
            Assert.Equal(row.ArchiveSha256, (await System.IO.File.ReadAllTextAsync(file + ".sha256")).Trim());
            return file;
        }

        private async Task<Guid> TakeAsync(WebApplication app, HttpClient client, CookieContainer cookies, int hostId)
        {
            var created = await GateDrive.SendJsonAsync(app, client, cookies, Host, hostId, HttpMethod.Post, "/api/Backup", new BackupPassphraseRequest { Passphrase = Passphrase }, _output, "take a snapshot");
            Assert.Equal(HttpStatusCode.Accepted, created.Status);
            var id = JsonSerializer.Deserialize<BackupOperationResult>(created.Body, Json)!.SnapshotId!.Value;
            for (var attempt = 0; attempt < 240; attempt++)
            {
                var row = await Engine(app).GetSnapshotAsync(id);
                if (row?.Status == BackupSnapshotStatus.Available)
                {
                    return id;
                }

                Assert.NotEqual(BackupSnapshotStatus.Failed, row?.Status);
                await Task.Delay(500);
            }

            throw new TimeoutException($"snapshot {id} never became Available.");
        }

        private static async Task UntilStagedAsync(WebApplication app, Guid id)
        {
            for (var attempt = 0; attempt < 240; attempt++)
            {
                if ((await Engine(app).GetSnapshotAsync(id))?.Status == BackupSnapshotStatus.Staged)
                {
                    return;
                }

                await Task.Delay(500);
            }

            throw new TimeoutException($"snapshot {id} was never staged.");
        }

        private static Task SignAsync(WebApplication app, string message) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
            {
                await sp.GetRequiredService<IRepository<GuestbookEntryAggregate, Guid>>().AddAsync(GuestbookEntryAggregate.Sign(sp.GetRequiredService<ITenantContext>().CurrentTenantId!, 3, "Ada", message));
                return 0;
            });

        private static Task<string[]> EntriesAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                (await sp.GetRequiredService<IRepository<GuestbookEntryAggregate, Guid>>().GetAllAsync()).Select(entry => entry.Message).OrderBy(m => m, StringComparer.Ordinal).ToArray());

        /// <summary>A new version of the home page's first HtmlText module - what the home page renders.</summary>
        private static Task SetHomeContentAsync(WebApplication app, string content) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var home = (await sp.GetRequiredService<IPageRepository>().GetPagesAsync(alias.SiteId)).OrderBy(page => page.Order).First(page => page.Path == string.Empty);
                var module = (await sp.GetRequiredService<IPageModuleRepository>().GetPageModulesAsync(alias.SiteId))
                    .Where(pm => pm.PageId == home.PageId && !pm.IsDeleted && pm.Module?.ModuleDefinitionName?.Contains("HtmlText", StringComparison.Ordinal) == true)
                    .OrderBy(pm => pm.PageModuleId).First();
                await sp.GetRequiredService<IHtmlTextRepository>().AddHtmlTextAsync(new Stomme.Modules.HtmlText.Models.HtmlText { ModuleId = module.ModuleId, Content = content });
                return 0;
            });

        /// <summary>A file in the site's Public folder, on disk and in the catalogue - served by <c>api/File/download</c>.</summary>
        private static Task<int> UploadAsync(WebApplication app, string name, byte[] content) =>
            GateDrive.InTenantAsync(app, Host, async (sp, alias) =>
            {
                var folder = await sp.GetRequiredService<IFolderRepository>().GetFolderAsync(alias.SiteId, "Public/");
                var path = await sp.GetRequiredService<IFolderRepository>().GetFolderPathAsync(folder);
                Directory.CreateDirectory(path);
                await System.IO.File.WriteAllBytesAsync(Path.Combine(path, name), content);
                var file = await sp.GetRequiredService<IFileRepository>().AddFileAsync(new Stomme.Models.File
                {
                    FolderId = folder.FolderId, Name = name, Extension = "txt", Size = content.Length, ImageHeight = 0, ImageWidth = 0, Description = string.Empty,
                });
                return file.FileId;
            });

        private static async Task<byte[]> DownloadAsync(HttpClient client, CookieContainer cookies, int fileId)
        {
            var uri = new Uri(new Uri("http://" + Host), $"/api/File/download/{fileId}");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Cookie", cookies.GetCookieHeader(uri));
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsByteArrayAsync();
        }
    }

    public sealed class SqliteInstalledSiteBackupDriveTests(ITestOutputHelper output) : InstalledSiteBackupDriveTests(output)
    {
        private protected override IGateStore Store() => new SqliteGateStore();
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class PostgresInstalledSiteBackupDriveTests(ITestOutputHelper output) : InstalledSiteBackupDriveTests(output)
    {
        private protected override IGateStore Store() => new PostgresGateStore();
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenInstalledSiteBackupDriveTests(ITestOutputHelper output) : InstalledSiteBackupDriveTests(output)
    {
        private protected override IGateStore Store() => new RavenGateStore();
    }

    /// <summary>BL-173: the same drive on SQL Server (infra UAT <c>:1434</c>) - installed fresh, it has no template clone.</summary>
    [Trait("Category", "ExternalDatabase")]
    public sealed class SqlServerInstalledSiteBackupDriveTests(ITestOutputHelper output) : InstalledSiteBackupDriveTests(output)
    {
        private protected override IGateStore Store() => new SqlServerGateStore();
    }
}
