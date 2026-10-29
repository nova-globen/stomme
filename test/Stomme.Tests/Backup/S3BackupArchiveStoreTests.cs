#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Modules.Backup.Domain;
using Stomme.Modules.Backup.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// What the S3 archive store adds to the contract: the upload in parts S3 accepts, a failed publish that leaves
    /// nothing behind, the key layout and its prefix, the configuration a deployment uses (the key pair from FILES),
    /// and the choice of store per snapshot.
    /// </summary>
    public sealed class S3BackupArchiveStoreTests : IDisposable
    {
        private const string Installation = "0f5e7a0c-installation";
        private const int Part = S3ArchiveOptions.MinimumPartBytes;

        private readonly string _scratch = Path.Combine(Path.GetTempPath(), "stomme-s3-store-" + Guid.NewGuid().ToString("N"));
        private readonly InMemoryS3ObjectClient _bucket = new();

        internal static S3ArchiveOptions Options(Action<S3ArchiveOptions>? change = null)
        {
            var options = new S3ArchiveOptions
            {
                Endpoint = "http://s3.test:7070",
                Bucket = "stomme-test-snapshots",
                Region = "home",
                AccessKeyId = "test-access-key",
                SecretAccessKey = "test-secret-key",
                PartSizeBytes = S3ArchiveOptions.MinimumPartBytes,
            };
            change?.Invoke(options);
            return options;
        }

        [Theory]
        [InlineData(1, new[] { 1 })]
        [InlineData(Part - 1, new[] { Part - 1 })]
        [InlineData(Part, new[] { Part })]
        [InlineData(Part + 1, new[] { Part, 1 })]
        [InlineData((2 * Part) + 12345, new[] { Part, Part, 12345 })]
        [InlineData(3 * Part, new[] { Part, Part, Part })]
        public async Task An_archive_is_uploaded_in_parts_S3_accepts_and_reads_back_byte_for_byte(int length, int[] parts)
        {
            var store = Store();
            var location = StorageLocation.ForSnapshot(Guid.NewGuid());
            var bytes = new byte[length];
            new Random(length).NextBytes(bytes);

            await WriteAsync(store, location, bytes);
            await store.PublishAsync(location, Checksum.Compute(bytes));

            // Every part but the last is at least 5 MiB - the in-memory bucket refuses the completion otherwise, as the real service does.
            Assert.Equal(parts, _bucket.PartsUploaded.Select(part => part.Length).ToArray());
            Assert.Equal(Enumerable.Range(1, parts.Length), _bucket.PartsUploaded.Select(part => part.PartNumber));
            Assert.Equal(0, _bucket.OpenUploads);
            Assert.Equal(bytes, _bucket.Objects[Installation + "/" + location.Value]);
            Assert.Empty(Directory.EnumerateFiles(store.StagingRoot));   // the staged copy is gone once the object exists

            await using var read = (await store.OpenReadAsync(location))!;
            using var copy = new MemoryStream();
            await read.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
        }

        [Fact]
        public async Task A_part_size_under_S3s_floor_is_refused_before_anything_is_sent()
        {
            var options = Options(o => o.PartSizeBytes = S3ArchiveOptions.MinimumPartBytes - 1);
            Assert.Contains("PartSizeBytes", options.Problem(), StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => options.ResolveCredentials());
        }

        [Theory]
        [InlineData("Start")]
        [InlineData("UploadPart:1")]
        [InlineData("UploadPart:2")]
        [InlineData("Put")]
        [InlineData("Complete")]
        public async Task A_publish_that_fails_anywhere_leaves_no_object_no_sidecar_and_no_open_upload(string failAt)
        {
            var store = Store();
            var location = StorageLocation.ForSnapshot(Guid.NewGuid());
            var bytes = new byte[Part + 10];
            await WriteAsync(store, location, bytes);

            _bucket.FailAt = failAt;
            await Assert.ThrowsAsync<IOException>(() => store.PublishAsync(location, Checksum.Compute(bytes)));

            Assert.Empty(_bucket.Objects);
            Assert.Equal(0, _bucket.OpenUploads);
            Assert.Empty(await store.ListAsync());

            // The staged write is still there for the engine to Abandon, as with the local store.
            store.Abandon(location);
            Assert.Empty(Directory.EnumerateFiles(store.StagingRoot));
        }

        [Fact]
        public async Task Keys_live_under_the_installation_unless_a_prefix_is_named_and_other_prefixes_are_not_listed()
        {
            var location = StorageLocation.ForSnapshot(Guid.NewGuid());
            var byInstallation = Store();
            Assert.Equal(Installation + "/" + location.Value, byInstallation.KeyOf(location));

            var named = Store(o => o.Prefix = "/uat/stomme/");
            Assert.Equal("uat/stomme/" + location.Value, named.KeyOf(location));

            await WriteAsync(byInstallation, location, new byte[] { 7 });
            await byInstallation.PublishAsync(location, Checksum.Compute(new byte[] { 7 }));
            _bucket.Plant("another-installation/" + StorageLocation.ForSnapshot(Guid.NewGuid()).Value, "theirs");
            _bucket.Plant(Installation + "-lookalike/" + StorageLocation.ForSnapshot(Guid.NewGuid()).Value, "theirs");
            _bucket.Plant(Installation + "/nested/" + StorageLocation.ForSnapshot(Guid.NewGuid()).Value, "not a name");

            Assert.Equal(new[] { location }, (await byInstallation.ListAsync()).ToArray());
            Assert.Empty(await named.ListAsync());
        }

        [Theory]
        [InlineData("..")]
        [InlineData("a/../b")]
        [InlineData("a\\b")]
        [InlineData("  /  ")]
        public void A_prefix_that_is_not_a_plain_path_is_refused(string prefix)
        {
            Assert.Throws<InvalidOperationException>(() => Options(o => o.Prefix = prefix).ResolvePrefix(Installation));
        }

        [Fact]
        public void An_installation_without_an_id_and_no_prefix_has_no_keys()
        {
            var store = new S3BackupArchiveStore(_bucket, Options(), () => string.Empty, Path.Combine(_scratch, "staging"));
            Assert.Throws<InvalidOperationException>(() => store.KeyOf(StorageLocation.ForSnapshot(Guid.NewGuid())));
        }

        [Fact]
        public void The_key_pair_is_read_from_files_first_trimmed_and_a_problem_names_the_setting_never_the_value()
        {
            Directory.CreateDirectory(_scratch);
            var idFile = Path.Combine(_scratch, "snapshots.access");
            var secretFile = Path.Combine(_scratch, "snapshots.secret");
            File.WriteAllText(idFile, "id-from-file\n");
            File.WriteAllText(secretFile, "  secret-from-file \r\n");

            var fromFiles = Options(o => { o.AccessKeyIdFile = idFile; o.SecretAccessKeyFile = secretFile; });
            Assert.Null(fromFiles.Problem());
            Assert.Equal(("id-from-file", "secret-from-file"), fromFiles.ResolveCredentials());

            Assert.Equal(("test-access-key", "test-secret-key"), Options().ResolveCredentials());

            var missingFile = Options(o => o.SecretAccessKeyFile = Path.Combine(_scratch, "absent"));
            Assert.Equal("Backup:Archive:S3:SecretAccessKeyFile names no file.", missingFile.Problem());

            var noSecret = Options(o => o.SecretAccessKey = " ");
            Assert.Equal("Backup:Archive:S3:SecretAccessKey is not set.", noSecret.Problem());

            File.WriteAllText(secretFile, "   ");
            var emptyFile = Options(o => o.SecretAccessKeyFile = secretFile);
            var refusal = Assert.Throws<InvalidOperationException>(() => emptyFile.ResolveCredentials());
            Assert.DoesNotContain("test-access-key", refusal.Message, StringComparison.Ordinal);

            foreach (var problem in new[] { Options(o => o.Endpoint = "s3.test:7070"), Options(o => o.Endpoint = "ftp://s3.test"), Options(o => o.Bucket = "") }.Select(o => o.Problem()))
            {
                Assert.NotNull(problem);
                Assert.DoesNotContain("test-secret-key", problem, StringComparison.Ordinal);
                Assert.StartsWith("Backup:Archive:S3:", problem, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void The_settings_infra_deploys_bind_from_configuration()
        {
            // The container's environment, as the deployment's compose file writes it (double underscores are the section separator).
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backup:Archive:Provider"] = "S3",
                ["Backup:Archive:S3:Endpoint"] = "http://s3-local:7070",
                ["Backup:Archive:S3:Bucket"] = "example-backups",
                ["Backup:Archive:S3:Region"] = "home",
                ["Backup:Archive:S3:ForcePathStyle"] = "true",
                ["Backup:Archive:S3:AccessKeyIdFile"] = "/run/secrets/snapshots.access",
                ["Backup:Archive:S3:SecretAccessKeyFile"] = "/run/secrets/snapshots.secret",
            }).Build();
            var options = new BackupArchiveOptions();
            configuration.GetSection(BackupArchiveOptions.SectionName).Bind(options);

            Assert.True(options.UsesS3);
            Assert.Equal("http://s3-local:7070", options.S3.Endpoint);
            Assert.Equal("example-backups", options.S3.Bucket);
            Assert.Equal("home", options.S3.Region);
            Assert.True(options.S3.ForcePathStyle);
            Assert.Equal("/run/secrets/snapshots.access", options.S3.AccessKeyIdFile);
            Assert.Equal("/run/secrets/snapshots.secret", options.S3.SecretAccessKeyFile);
            Assert.Equal(S3ArchiveOptions.DefaultPartBytes, options.S3.PartSizeBytes);

            // No file on this machine: the problem names the setting, and the store is not offered.
            Assert.Equal("Backup:Archive:S3:AccessKeyIdFile names no file.", options.S3.Problem());
            Assert.False(new BackupArchiveOptions().UsesS3);
        }

        [Fact]
        public void A_snapshot_is_read_from_the_store_its_row_names_and_new_ones_go_where_the_provider_says()
        {
            var local = new LocalFileBackupArchiveStore(Path.Combine(_scratch, "backups"));
            var s3 = Store();
            var now = DateTime.UtcNow;
            var legacy = BackupSnapshotAggregate.Start(Guid.NewGuid(), RetentionTag.Manual, "10.2.3", now);
            var onS3 = BackupSnapshotAggregate.Start(Guid.NewGuid(), RetentionTag.Manual, "10.2.3", now, BackupArchiveOptions.S3Provider);

            var toS3 = new BackupArchiveStores(local, () => s3, null, "s3");
            Assert.Equal("S3", toS3.CurrentName);
            Assert.Same(s3, toS3.Current);
            Assert.Same(local, toS3.For(legacy));
            Assert.Same(s3, toS3.For(onS3));
            Assert.Same(local, toS3.For((string?)null));   // a row written before the field existed

            var toLocal = new BackupArchiveStores(local, null, "Backup:Archive:S3:Bucket is not set.", null);
            Assert.Equal("Local", toLocal.CurrentName);
            Assert.Same(local, toLocal.Current);
            var unreadable = Assert.Throws<InvalidOperationException>(() => toLocal.For(onS3));
            Assert.Contains("Backup:Archive:S3:Bucket is not set.", unreadable.Message, StringComparison.Ordinal);

            // Fail-closed: asked for S3 and not configured, or asked for a store that does not exist - never quietly Local.
            Assert.Throws<InvalidOperationException>(() => new BackupArchiveStores(local, null, "Backup:Archive:S3:Endpoint is not an http(s) address.", "S3").Current);
            Assert.Throws<InvalidOperationException>(() => new BackupArchiveStores(local, () => s3, null, "Azure").CurrentName);
            Assert.Throws<InvalidOperationException>(() => toS3.For("Azure"));
        }

        [Fact]
        public void A_row_records_its_store_and_refuses_a_name_that_is_not_one()
        {
            var now = DateTime.UtcNow;
            Assert.Equal("Local", BackupSnapshotAggregate.Start(Guid.NewGuid(), RetentionTag.Manual, "10.2.3", now).ArchiveStore);
            Assert.Equal("S3", BackupSnapshotAggregate.Imported(Guid.NewGuid(), "10.2.3", now, Checksum.Compute(new byte[] { 1 }), 1, now, "S3").ArchiveStore);
            Assert.Throws<ArgumentException>(() => BackupSnapshotAggregate.Start(Guid.NewGuid(), RetentionTag.Manual, "10.2.3", now, "../s3"));
            Assert.Throws<ArgumentException>(() => BackupSnapshotAggregate.Start(Guid.NewGuid(), RetentionTag.Manual, "10.2.3", now, " "));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_scratch))
                {
                    Directory.Delete(_scratch, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a green run over.
            }
        }

        private S3BackupArchiveStore Store(Action<S3ArchiveOptions>? change = null) =>
            new(_bucket, Options(change), () => Installation, Path.Combine(_scratch, "staging"));

        private static async Task WriteAsync(IBackupArchiveStore store, StorageLocation location, byte[] bytes)
        {
            await using var stream = store.Create(location);
            await stream.WriteAsync(bytes);
        }
    }
}
