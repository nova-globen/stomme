#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Modules.Backup.Domain;
using Stomme.Modules.Backup.Infrastructure;
using Xunit;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// BL-81 and BL-169 - the archive store contract, run against the local folder AND the S3 store over an
    /// in-memory bucket that keeps S3's rules: an archive is invisible until PUBLISHED (a snapshot that dies mid-write leaves
    /// nothing that looks whole), carries its checksum sidecar, deletes with it, and lists only published archives. Every
    /// name is a <see cref="StorageLocation"/>, so nothing resolves outside the root or the prefix.
    /// the predecessor platform's <c>LocalFileSystemBackupArchiveStore</c> path-escape guard lands here.
    /// </summary>
    public abstract class BackupArchiveStoreContractTests : IDisposable
    {
        protected readonly string Scratch = Path.Combine(Path.GetTempPath(), "stomme-backup-store-" + Guid.NewGuid().ToString("N"));

        protected abstract IBackupArchiveStore Store { get; }

        /// <summary>Everything the store holds for any name, published or not - empty after a delete or an abandon.</summary>
        protected abstract string[] Leftovers();

        /// <summary>Overwrites the checksum sidecar of <paramref name="location"/> with text that is not a checksum.</summary>
        protected abstract void TamperSidecar(StorageLocation location);

        /// <summary>Puts an object/file whose name is not a storage location beside the archives.</summary>
        protected abstract void PlantStray(string name);

        [Fact]
        public async Task An_archive_is_invisible_until_published_then_reads_back_with_its_checksum()
        {
            var location = StorageLocation.ForSnapshot(Guid.NewGuid());
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            await using (var stream = Store.Create(location))
            {
                await stream.WriteAsync(bytes);
            }

            Assert.Null(await Store.OpenReadAsync(location));
            Assert.Empty(await Store.ListAsync());
            Assert.Null(await Store.ReadChecksumAsync(location));

            var checksum = Checksum.Compute(bytes);
            await Store.PublishAsync(location, checksum);
            Assert.Equal(location, Assert.Single(await Store.ListAsync()));
            Assert.Equal(checksum, await Store.ReadChecksumAsync(location));
            await using (var read = (await Store.OpenReadAsync(location))!)
            {
                Assert.True(read.CanSeek);
                Assert.Equal(checksum, await Checksum.ComputeAsync(read));
                read.Position = 0;
                Assert.Equal(checksum, await Checksum.ComputeAsync(read));
            }

            await Store.DeleteAsync(location);
            Assert.Null(await Store.OpenReadAsync(location));
            Assert.Empty(Leftovers());
        }

        [Fact]
        public async Task An_abandoned_write_leaves_nothing_and_a_name_is_written_once()
        {
            var location = StorageLocation.ForSnapshot(Guid.NewGuid());
            await using (var stream = Store.Create(location))
            {
                await stream.WriteAsync(new byte[] { 9 });
                Assert.Throws<IOException>(() => Store.Create(location));
            }

            Store.Abandon(location);
            Assert.Empty(Leftovers());
            await Assert.ThrowsAsync<FileNotFoundException>(() => Store.PublishAsync(location, Checksum.Compute(new byte[] { 9 })));
        }

        [Fact]
        public async Task A_tampered_sidecar_reads_as_no_checksum_and_stray_names_are_not_listed()
        {
            var location = StorageLocation.ForSnapshot(Guid.NewGuid());
            await using (var stream = Store.Create(location))
            {
                await stream.WriteAsync(new byte[] { 1 });
            }

            await Store.PublishAsync(location, Checksum.Compute(new byte[] { 1 }));
            TamperSidecar(location);
            Assert.Null(await Store.ReadChecksumAsync(location));

            PlantStray("has space.dcbak");
            Assert.Equal(new[] { location }, (await Store.ListAsync()).ToArray());
        }

        [Fact]
        public async Task Deleting_what_is_not_there_is_not_an_error()
        {
            await Store.DeleteAsync(StorageLocation.ForSnapshot(Guid.NewGuid()));
            Assert.Empty(Leftovers());
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Scratch))
                {
                    Directory.Delete(Scratch, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a green run over.
            }
        }
    }

    public sealed class LocalFileBackupArchiveStoreTests : BackupArchiveStoreContractTests
    {
        private readonly LocalFileBackupArchiveStore _store;

        public LocalFileBackupArchiveStoreTests() => _store = new LocalFileBackupArchiveStore(Path.Combine(Scratch, "backups"));

        protected override IBackupArchiveStore Store => _store;

        protected override string[] Leftovers() => Directory.Exists(_store.Root) ? Directory.EnumerateFiles(_store.Root).ToArray() : Array.Empty<string>();

        protected override void TamperSidecar(StorageLocation location) =>
            File.WriteAllText(Path.Combine(_store.Root, location.Value + ".sha256"), "not a checksum");

        protected override void PlantStray(string name) => File.WriteAllText(Path.Combine(_store.Root, name), "x");

        [Fact]
        public void The_root_is_the_data_folder_named_backups()
        {
            Assert.Equal("backups", LocalFileBackupArchiveStore.FolderName);
            Assert.Equal(Path.GetFullPath(Path.Combine(Scratch, "backups")), _store.Root);
        }
    }

    public sealed class S3BackupArchiveStoreContractTests : BackupArchiveStoreContractTests
    {
        private const string Installation = "installation-under-test";

        private readonly InMemoryS3ObjectClient _bucket = new();
        private readonly S3BackupArchiveStore _store;

        public S3BackupArchiveStoreContractTests() =>
            _store = new S3BackupArchiveStore(_bucket, S3BackupArchiveStoreTests.Options(), () => Installation, Path.Combine(Scratch, "staging"));

        protected override IBackupArchiveStore Store => _store;

        protected override string[] Leftovers() =>
            _bucket.Objects.Keys
                .Concat(Directory.Exists(_store.StagingRoot) ? Directory.EnumerateFiles(_store.StagingRoot) : Array.Empty<string>())
                .Concat(Enumerable.Repeat("open multipart upload", _bucket.OpenUploads))
                .ToArray();

        protected override void TamperSidecar(StorageLocation location) => _bucket.Plant(_store.KeyOf(location) + ".sha256", "not a checksum");

        protected override void PlantStray(string name) => _bucket.Plant(Installation + "/" + name, "x");
    }
}
