#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Modules.Backup.Application;
using Stomme.Modules.Backup.Domain;
using Stomme.Modules.Backup.Infrastructure;
using Stomme.Modules.Guestbook.Domain;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// BL-169 - the object-store archive backend DRIVEN on an installed host: the real engine, the real boot phase, the
    /// settings in the override FILE (BL-162), and an in-memory bucket in place of the SDK (the live round trip against
    /// infra's store is <c>test/Stomme.Backup.S3.Live.Tests</c>, outside the solution). A snapshot taken to the bucket is
    /// restored from it after a restart; switching the provider leaves an earlier snapshot readable where it is; and a
    /// provider of S3 without its settings fails the snapshot rather than writing it locally.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public sealed class S3ArchiveBackupDriveTests
    {
        private const string Host = "localhost";
        private const string Passphrase = "object store passphrase";

        private readonly ITestOutputHelper _output;

        public S3ArchiveBackupDriveTests(ITestOutputHelper output) => _output = output;

        private static JsonObject S3Settings(string? bucket = "stomme-drive-snapshots") => new()
        {
            ["Provider"] = "S3",
            ["S3"] = new JsonObject
            {
                ["Endpoint"] = "http://s3.test:7070",
                ["Bucket"] = bucket,
                ["Region"] = "home",
                ["AccessKeyId"] = "drive-access-key",
                ["SecretAccessKey"] = "drive-secret-key",
            },
        };

        [Fact]
        public async Task A_snapshot_taken_to_the_object_store_is_restored_from_it_after_a_restart()
        {
            var bucket = new InMemoryS3ObjectClient();
            using var scratch = await ScratchAsync();
            SetArchive(scratch, S3Settings());

            Guid snapshot;
            string key;
            var first = await InstallAsync(scratch, bucket);
            try
            {
                await SignAsync(first, "before the snapshot");
                snapshot = await Engine(first).CreateSnapshotAsync(Passphrase, RetentionTag.Manual);
                var row = (await Engine(first).GetSnapshotAsync(snapshot))!;
                var config = first.Services.GetRequiredService<IConfigManager>();
                key = config.GetInstallationId() + "/" + row.StorageLocation.Value;
                _output.WriteLine($"[s3] snapshot {snapshot} at {key}: {row.SizeBytes} bytes, store {row.ArchiveStore}");

                Assert.Equal(BackupSnapshotStatus.Available, row.Status);
                Assert.Equal("S3", row.ArchiveStore);
                Assert.Equal(row.SizeBytes, bucket.Objects[key].LongLength);
                Assert.Equal(row.ArchiveSha256, System.Text.Encoding.UTF8.GetString(bucket.Objects[key + ".sha256"]));
                Assert.Equal(0, bucket.OpenUploads);
                Assert.Empty(LocalArchives(config));   // nothing written to the local store, and nothing left staged

                await SignAsync(first, "after the snapshot");
                var staged = await Engine(first).StageRestoreAsync(snapshot, Passphrase);
                Assert.True(staged.Ok, string.Join("; ", staged.Problems));
                Assert.Equal(2, bucket.Objects.Keys.Count(k => k.EndsWith(".dcbak", StringComparison.Ordinal)));   // the safety snapshot went there too
                Assert.Empty(LocalArchives(config));
            }
            finally
            {
                await GateDrive.ShutdownAsync(first);
            }

            var second = await InstallAsync(scratch, bucket);
            try
            {
                Assert.Equal(new[] { "before the snapshot" }, await EntriesAsync(second));
                var row = (await Engine(second).GetSnapshotAsync(snapshot))!;
                Assert.Equal(BackupSnapshotStatus.Available, row.Status);
                Assert.NotNull(row.LastRestoredOnUtc);

                using var copy = new MemoryStream();
                await using (var download = (await Engine(second).OpenArchiveAsync(snapshot))!)
                {
                    await download.CopyToAsync(copy);

                    // While the download is open its temporary copy exists; closing it removes it.
                    Assert.Single(LocalArchives(second.Services.GetRequiredService<IConfigManager>()));
                }

                Assert.Equal(bucket.Objects[key], copy.ToArray());
                Assert.Empty(LocalArchives(second.Services.GetRequiredService<IConfigManager>()));
            }
            finally
            {
                await GateDrive.ShutdownAsync(second);
            }
        }

        [Fact]
        public async Task Switching_the_provider_leaves_earlier_snapshots_where_they_are_and_S3_without_settings_writes_nothing()
        {
            var bucket = new InMemoryS3ObjectClient();
            using var scratch = await ScratchAsync();

            Guid onLocal;
            var local = await InstallAsync(scratch, bucket);
            try
            {
                onLocal = await Engine(local).CreateSnapshotAsync(Passphrase, RetentionTag.Manual);
                Assert.Equal("Local", (await Engine(local).GetSnapshotAsync(onLocal))!.ArchiveStore);
                Assert.Single(LocalArchives(local.Services.GetRequiredService<IConfigManager>()));
            }
            finally
            {
                await GateDrive.ShutdownAsync(local);
            }

            // Provider S3, but the bucket is not set: the snapshot FAILS - it is not quietly written to the local folder.
            SetArchive(scratch, S3Settings(bucket: null));
            var misconfigured = await InstallAsync(scratch, bucket);
            try
            {
                var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Engine(misconfigured).CreateSnapshotAsync(Passphrase, RetentionTag.Manual));
                _output.WriteLine($"[s3 without a bucket] {refused.Message}");
                Assert.Contains("Backup:Archive:S3:Bucket", refused.Message, StringComparison.Ordinal);
                var failed = (await Engine(misconfigured).ListSnapshotsAsync()).Single(row => row.Id != onLocal);
                Assert.Equal(BackupSnapshotStatus.Failed, failed.Status);
                Assert.Single(LocalArchives(misconfigured.Services.GetRequiredService<IConfigManager>()));
                Assert.Empty(bucket.Objects);

                // The earlier snapshot is still read from the store its row names.
                Assert.True((await Engine(misconfigured).PreflightAsync(onLocal, Passphrase)).Ok);
            }
            finally
            {
                await GateDrive.ShutdownAsync(misconfigured);
            }

            SetArchive(scratch, S3Settings());
            var s3 = await InstallAsync(scratch, bucket);
            try
            {
                var engine = Engine(s3);
                var onS3 = await engine.CreateSnapshotAsync(Passphrase, RetentionTag.Manual);
                Assert.Equal("S3", (await engine.GetSnapshotAsync(onS3))!.ArchiveStore);
                Assert.True((await engine.PreflightAsync(onLocal, Passphrase)).Ok);
                Assert.True((await engine.PreflightAsync(onS3, Passphrase)).Ok);

                await engine.DeleteSnapshotAsync(onLocal);
                Assert.Empty(LocalArchives(s3.Services.GetRequiredService<IConfigManager>()));
                Assert.Equal(2, bucket.Objects.Count);   // the S3 archive and its sidecar, untouched by the local delete

                await engine.DeleteSnapshotAsync(onS3);
                Assert.Empty(bucket.Objects);
            }
            finally
            {
                await GateDrive.ShutdownAsync(s3);
            }
        }

        private Task<ScratchGateInstall> ScratchAsync() =>
            GateInstallTemplates.CloneAsync(new SqliteGateStore(), Host, _output, colocateModuleAggregates: false,
                extraSettings: new JsonObject { ["Backup"] = new JsonObject { ["Enabled"] = true } });

        private Task<WebApplication> InstallAsync(ScratchGateInstall scratch, InMemoryS3ObjectClient bucket) =>
            GateDrive.InstallAsync(scratch, _output, builder => builder.Services.AddSingleton<IS3ObjectClient>(bucket));

        /// <summary>Writes <c>Backup:Archive</c> into the install's override FILE - where a deployed host keeps it.</summary>
        private static void SetArchive(ScratchGateInstall scratch, JsonObject archive)
        {
            var path = Path.Combine(scratch.ContentRoot, "appsettings.override.json");
            var settings = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var backup = settings["Backup"] as JsonObject ?? new JsonObject();
            backup["Archive"] = archive.DeepClone();
            settings["Backup"] = backup;
            File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>The archives in the local store and anything left in the S3 store's staging folder.</summary>
        private static string[] LocalArchives(IConfigManager config)
        {
            var root = Path.Combine(config.GetDataDirectory(), LocalFileBackupArchiveStore.FolderName);
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(file => !file.EndsWith("restore.pending.json", StringComparison.Ordinal) && !file.EndsWith(".sha256", StringComparison.Ordinal)).ToArray()
                : Array.Empty<string>();
        }

        private static BackupEngine Engine(WebApplication app) => app.Services.CreateScope().ServiceProvider.GetRequiredService<BackupEngine>();

        private static Task SignAsync(WebApplication app, string message) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
            {
                await sp.GetRequiredService<IRepository<GuestbookEntryAggregate, Guid>>().AddAsync(GuestbookEntryAggregate.Sign(sp.GetRequiredService<ITenantContext>().CurrentTenantId!, 3, "Ada", message));
                return 0;
            });

        private static Task<string[]> EntriesAsync(WebApplication app) =>
            GateDrive.InTenantAsync(app, Host, async (sp, _) =>
                (await sp.GetRequiredService<IRepository<GuestbookEntryAggregate, Guid>>().GetAllAsync()).Select(entry => entry.Message).OrderBy(m => m, StringComparer.Ordinal).ToArray());
    }
}
