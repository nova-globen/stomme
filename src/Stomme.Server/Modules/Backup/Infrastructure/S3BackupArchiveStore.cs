#nullable enable
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Backup.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Backup.Infrastructure
{
    /// <summary>
    /// Archives in an S3-compatible object store (BL-169, D106 item 9): the key of an archive is <c>&lt;prefix&gt;/&lt;name&gt;</c>
    /// and its checksum sidecar <c>&lt;prefix&gt;/&lt;name&gt;.sha256</c>, the prefix being the installation's id unless the
    /// operator names one. Nothing a row holds becomes a key but a <see cref="StorageLocation"/>, which admits no separator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It STAGES through a local file.</b> The archive writer and reader are synchronous over their stream (a GCM chunk at a
    /// time) and sync-over-async is banned (D33), so an upload cannot be a stream the writer pushes into, nor a download one
    /// the reader pulls from. <see cref="Create"/> writes a staging file under the host's data folder; <see cref="PublishAsync"/>
    /// uploads it in multipart parts of <see cref="S3ArchiveOptions.PartSizeBytes"/> (one part's buffer is the heap it
    /// holds), puts the sidecar, completes the upload - the object appears whole or not at all - and deletes the staging
    /// file. A read downloads the object to a temporary file that deletes itself on close. The disk a snapshot needs is
    /// therefore the archive's size, as with the local store.
    /// </para>
    /// <para>
    /// <b>Publish order.</b> The sidecar is written BEFORE the upload completes, as the local store writes it before the move:
    /// a listed archive always has its checksum. A failed publish aborts the upload (no part is left billed or visible) and
    /// removes the sidecar.
    /// </para>
    /// <para>
    /// <b>The endpoint is operator configuration</b>, like a database connection string, so it is dialled directly and never
    /// through the host's outbound guard, which refuses private addresses - where a LAN object store lives.
    /// </para>
    /// </remarks>
    public sealed class S3BackupArchiveStore : IBackupArchiveStore
    {
        public const string StagingFolderName = "staging";
        private const string PartialSuffix = ".partial";
        private const string DownloadSuffix = ".download";
        private const string SidecarSuffix = ".sha256";

        private readonly IS3ObjectClient _client;
        private readonly S3ArchiveOptions _options;
        private readonly Func<string> _installationId;
        private readonly string _staging;
        private readonly ILogger? _logger;

        public S3BackupArchiveStore(IS3ObjectClient client, S3ArchiveOptions options, Func<string> installationId, string stagingRoot, ILogger? logger = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _installationId = installationId ?? throw new ArgumentNullException(nameof(installationId));
            if (string.IsNullOrWhiteSpace(stagingRoot))
            {
                throw new ArgumentException("The S3 archive store stages through a local folder.", nameof(stagingRoot));
            }

            _staging = Path.GetFullPath(stagingRoot);
            _logger = logger;
        }

        public string StagingRoot => _staging;

        public Stream Create(StorageLocation location)
        {
            Directory.CreateDirectory(_staging);
            return new FileStream(StagingPath(location, PartialSuffix), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        }

        public async Task PublishAsync(StorageLocation location, Checksum checksum, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(checksum);
            var staged = StagingPath(location, PartialSuffix);
            if (!File.Exists(staged))
            {
                throw new FileNotFoundException($"There is no written archive '{location}' to publish.");
            }

            var key = KeyOf(location);
            var uploadId = await _client.StartMultipartAsync(key, cancellationToken);
            try
            {
                var parts = new List<(int PartNumber, string ETag)>();
                var buffer = ArrayPool<byte>.Shared.Rent(_options.PartSizeBytes);
                try
                {
                    await using var file = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    while (true)
                    {
                        var filled = await FillAsync(file, buffer, _options.PartSizeBytes, cancellationToken);
                        if (filled == 0 && parts.Count > 0)
                        {
                            break;
                        }

                        parts.Add((parts.Count + 1, await _client.UploadPartAsync(key, uploadId, parts.Count + 1, buffer.AsMemory(0, filled), cancellationToken)));
                        if (filled < _options.PartSizeBytes)
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                }

                await _client.PutTextAsync(key + SidecarSuffix, checksum.Value, cancellationToken);
                await _client.CompleteMultipartAsync(key, uploadId, parts, cancellationToken);
            }
            catch
            {
                await BestEffortAsync(() => _client.AbortMultipartAsync(key, uploadId, CancellationToken.None), "abort the upload of", location);
                await BestEffortAsync(() => _client.DeleteAsync(key + SidecarSuffix, CancellationToken.None), "remove the sidecar of", location);
                throw;
            }

            DeleteIfPresent(staged);
        }

        public void Abandon(StorageLocation location) => DeleteIfPresent(StagingPath(location, PartialSuffix));

        public async Task<Stream?> OpenReadAsync(StorageLocation location, CancellationToken cancellationToken = default)
        {
            Directory.CreateDirectory(_staging);
            var path = StagingPath(location, "." + Guid.NewGuid().ToString("N") + DownloadSuffix);
            var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            try
            {
                if (!await _client.DownloadAsync(KeyOf(location), file, cancellationToken))
                {
                    await file.DisposeAsync();
                    return null;
                }

                await file.FlushAsync(cancellationToken);
                file.Position = 0;
                return file;
            }
            catch
            {
                await file.DisposeAsync();
                throw;
            }
        }

        public async Task<Checksum?> ReadChecksumAsync(StorageLocation location, CancellationToken cancellationToken = default)
        {
            var text = await _client.GetTextAsync(KeyOf(location) + SidecarSuffix, cancellationToken);
            if (text is null)
            {
                return null;
            }

            try
            {
                return Checksum.Create(text);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        public async Task DeleteAsync(StorageLocation location, CancellationToken cancellationToken = default)
        {
            var key = KeyOf(location);
            await _client.DeleteAsync(key, cancellationToken);
            await _client.DeleteAsync(key + SidecarSuffix, cancellationToken);
            DeleteIfPresent(StagingPath(location, PartialSuffix));
        }

        public async Task<IReadOnlyList<StorageLocation>> ListAsync(CancellationToken cancellationToken = default)
        {
            var prefix = Prefix() + "/";
            return (await _client.ListKeysAsync(prefix, cancellationToken))
                .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(key => key[prefix.Length..])
                .Where(name => !name.EndsWith(SidecarSuffix, StringComparison.Ordinal) && name.Length <= StorageLocation.MaxLength && StorageLocation.IsFileName(name))
                .Select(StorageLocation.Create)
                .OrderBy(location => location.Value, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>The object key of an archive - its prefix and its name, nothing else.</summary>
        public string KeyOf(StorageLocation location)
        {
            ArgumentNullException.ThrowIfNull(location);
            return Prefix() + "/" + location.Value;
        }

        private string Prefix() => _options.ResolvePrefix(_installationId());

        private string StagingPath(StorageLocation location, string suffix)
        {
            ArgumentNullException.ThrowIfNull(location);
            var path = Path.GetFullPath(Path.Combine(_staging, location.Value + suffix));
            if (!string.Equals(Path.GetDirectoryName(path), _staging.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"'{location}' does not resolve inside the staging folder.");
            }

            return path;
        }

        private static async Task<int> FillAsync(Stream source, byte[] buffer, int count, CancellationToken cancellationToken)
        {
            var filled = 0;
            while (filled < count)
            {
                var read = await source.ReadAsync(buffer.AsMemory(filled, count - filled), cancellationToken);
                if (read == 0)
                {
                    break;
                }

                filled += read;
            }

            return filled;
        }

        private async Task BestEffortAsync(Func<Task> action, string what, StorageLocation location)
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "The S3 archive store could not {What} {Location}.", what, location.Value);
            }
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
