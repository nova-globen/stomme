#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Modules.Backup.Domain;

namespace Stomme.Modules.Backup.Infrastructure
{
    /// <summary>
    /// Where archives are kept: write-then-publish, read, delete, list - by a file NAME
    /// (<see cref="StorageLocation"/>), never a path. Two implementations: the local folder and an S3-compatible
    /// object store; <see cref="BackupArchiveStores"/> says which one holds a snapshot.
    /// </summary>
    /// <remarks>
    /// Writing is a synchronous-looking <see cref="Create"/> because the archive writer pushes into a stream; every operation
    /// that may cross a network is asynchronous (sync-over-async is banned).
    /// </remarks>
    public interface IBackupArchiveStore
    {
        /// <summary>
        /// A stream for a NEW archive, written beside its final name and invisible until <see cref="PublishAsync"/> - so a
        /// snapshot that dies mid-write leaves no archive that looks whole.
        /// </summary>
        Stream Create(StorageLocation location);

        /// <summary>Makes a written archive visible under its name, with its checksum sidecar.</summary>
        Task PublishAsync(StorageLocation location, Checksum checksum, CancellationToken cancellationToken = default);

        /// <summary>Discards an unpublished archive.</summary>
        void Abandon(StorageLocation location);

        /// <summary>A published archive, read-only and seekable; null when there is none.</summary>
        Task<Stream?> OpenReadAsync(StorageLocation location, CancellationToken cancellationToken = default);

        /// <summary>The checksum recorded beside a published archive; null when there is none.</summary>
        Task<Checksum?> ReadChecksumAsync(StorageLocation location, CancellationToken cancellationToken = default);

        /// <summary>Removes an archive, its sidecar and any unpublished write of it; removing what is not there is not an error.</summary>
        Task DeleteAsync(StorageLocation location, CancellationToken cancellationToken = default);

        /// <summary>The published archives, by name.</summary>
        Task<IReadOnlyList<StorageLocation>> ListAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Archives under <c>|DataDirectory|backups</c> - the content root's <c>Data</c> folder, which is NOT web-served (the
    /// web root's <c>Content</c> is; D106 refutation 17). A name is a <see cref="StorageLocation"/>, which admits no
    /// separator and no "..", and every resolved path is checked to sit directly in the root besides.
    /// </summary>
    public sealed class LocalFileBackupArchiveStore : IBackupArchiveStore
    {
        public const string FolderName = "backups";
        private const string PartialSuffix = ".partial";
        private const string SidecarSuffix = ".sha256";

        private readonly string _root;

        public LocalFileBackupArchiveStore(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new ArgumentException("The archive store has a root.", nameof(root));
            }

            _root = Path.GetFullPath(root);
        }

        public static LocalFileBackupArchiveStore ForHost(IConfigManager config) =>
            new(Path.Combine(config.GetDataDirectory(), FolderName));

        public string Root => _root;

        public Stream Create(StorageLocation location)
        {
            Directory.CreateDirectory(_root);
            return new FileStream(PathOf(location, PartialSuffix), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        }

        public async Task PublishAsync(StorageLocation location, Checksum checksum, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(checksum);
            var partial = PathOf(location, PartialSuffix);
            if (!File.Exists(partial))
            {
                throw new FileNotFoundException($"There is no written archive '{location}' to publish.");
            }

            await File.WriteAllTextAsync(PathOf(location, SidecarSuffix), checksum.Value, cancellationToken);
            File.Move(partial, PathOf(location, string.Empty), overwrite: false);
        }

        public void Abandon(StorageLocation location) => DeleteIfPresent(PathOf(location, PartialSuffix));

        public Task<Stream?> OpenReadAsync(StorageLocation location, CancellationToken cancellationToken = default)
        {
            var path = PathOf(location, string.Empty);
            return Task.FromResult<Stream?>(File.Exists(path)
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous)
                : null);
        }

        public async Task<Checksum?> ReadChecksumAsync(StorageLocation location, CancellationToken cancellationToken = default)
        {
            var path = PathOf(location, SidecarSuffix);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return Checksum.Create(await File.ReadAllTextAsync(path, cancellationToken));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        public Task DeleteAsync(StorageLocation location, CancellationToken cancellationToken = default)
        {
            DeleteIfPresent(PathOf(location, string.Empty));
            DeleteIfPresent(PathOf(location, SidecarSuffix));
            DeleteIfPresent(PathOf(location, PartialSuffix));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StorageLocation>> ListAsync(CancellationToken cancellationToken = default)
        {
            if (!Directory.Exists(_root))
            {
                return Task.FromResult<IReadOnlyList<StorageLocation>>(Array.Empty<StorageLocation>());
            }

            return Task.FromResult<IReadOnlyList<StorageLocation>>(Directory.EnumerateFiles(_root)
                .Select(Path.GetFileName)
                .Where(name => name is not null && !name.EndsWith(PartialSuffix, StringComparison.Ordinal) && !name.EndsWith(SidecarSuffix, StringComparison.Ordinal)
                    && name.Length <= StorageLocation.MaxLength && StorageLocation.IsFileName(name))
                .Select(name => StorageLocation.Create(name!))
                .OrderBy(location => location.Value, StringComparer.Ordinal)
                .ToList());
        }

        private string PathOf(StorageLocation location, string suffix)
        {
            ArgumentNullException.ThrowIfNull(location);
            var path = Path.GetFullPath(Path.Combine(_root, location.Value + suffix));
            if (!string.Equals(Path.GetDirectoryName(path), _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"'{location}' does not resolve inside the archive store.");
            }

            return path;
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
