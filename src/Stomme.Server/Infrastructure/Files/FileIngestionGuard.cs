#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;
using File = System.IO.File;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Stomme.Infrastructure
{
    /// <summary>Why the file-ingestion guard refused a file.</summary>
    public enum FileIngestionRefusalReason
    {
        None,
        InvalidName,
        Extension,
        InvalidChunk,
        Size,
        TotalSize,
        EntryCount,
        Ratio,
        Threat,
        ScanFailed,
        Capacity,
        Corrupt,
        Destination,
        Unreachable
    }

    /// <summary>The guard's answer: accepted (with the file names that reached the folder), pending (a chunk set still arriving) or refused.</summary>
    public sealed class FileIngestionResult
    {
        private FileIngestionResult(bool accepted, bool pending, IReadOnlyList<string> files, IReadOnlyList<string> skipped, FileIngestionRefusalReason reason, string message)
        {
            IsAccepted = accepted;
            IsPending = pending;
            Files = files;
            Skipped = skipped;
            Reason = reason;
            Message = message;
        }

        public bool IsAccepted { get; }

        public bool IsPending { get; }

        public bool IsRefused => !IsAccepted && !IsPending;

        /// <summary>The names now in the target folder, when accepted.</summary>
        public IReadOnlyList<string> Files { get; }

        /// <summary>Zip entries skipped for their name or extension (the historical unzip behaviour), when accepted.</summary>
        public IReadOnlyList<string> Skipped { get; }

        public FileIngestionRefusalReason Reason { get; }

        public string Message { get; }

        public static FileIngestionResult Accepted(IReadOnlyList<string> files, IReadOnlyList<string>? skipped = null) =>
            new(true, false, files, skipped ?? Array.Empty<string>(), FileIngestionRefusalReason.None, "");

        public static FileIngestionResult Pending() =>
            new(false, true, Array.Empty<string>(), Array.Empty<string>(), FileIngestionRefusalReason.None, "");

        public static FileIngestionResult Refused(FileIngestionRefusalReason reason, string message) =>
            new(false, false, Array.Empty<string>(), Array.Empty<string>(), reason, message);
    }

    /// <summary>Where accepted files go: the site (for the extension allowlist and staging), the folder row (null for a Host path upload) and its directory.</summary>
    public sealed record FileIngestionTarget(Alias Alias, Folder? Folder, string Directory);

    /// <summary>
    /// The ONE ingestion pipeline every path that brings bytes into a folder runs through (BL-100 F1, D109 items 1, 10,
    /// 14, 15): extension allowlist, then size, then scan, then (for an archive) the entry-count / decompressed-total /
    /// ratio caps, then the folder capacity - all on a STAGING copy outside every folder, which is moved into the
    /// target folder only once every check has passed. A refusal leaves the folder exactly as it was.
    /// </summary>
    public interface IFileIngestionGuard
    {
        /// <summary>The caps in force for this request (the <c>Files</c> configuration section).</summary>
        FileIngestionOptions Options { get; }

        /// <summary>The name check every path applies: a valid path segment with an extension in the site's <c>UploadableFiles</c>.</summary>
        Task<bool> IsAllowedFileNameAsync(Alias alias, string fileName);

        /// <summary>One part of a (possibly single-part) upload. Parts are staged; the set is merged and ingested once complete.</summary>
        Task<FileIngestionResult> AcceptChunkAsync(FileIngestionTarget target, string fileName, int partNumber, int totalParts, Stream content, long length, CancellationToken cancellationToken);

        /// <summary>Extracts an archive already on disk into the target folder, counting real decompressed bytes.</summary>
        Task<FileIngestionResult> ExtractZipAsync(FileIngestionTarget target, string zipPath, CancellationToken cancellationToken);

        /// <summary>Downloads a URL through the outbound connect guard into the target folder.</summary>
        Task<FileIngestionResult> DownloadAsync(FileIngestionTarget target, string url, string fileName, CancellationToken cancellationToken);

        /// <summary>True when adding <paramref name="incomingBytes"/> (replacing the named files) takes the folder to or over its capacity.</summary>
        Task<bool> ExceedsCapacityAsync(Folder folder, long incomingBytes, IReadOnlyCollection<string> replacing);
    }

    /// <inheritdoc cref="IFileIngestionGuard"/>
    /// <remarks>
    /// <para>
    /// <b>Why one service.</b> Refutation 15: inline guards cannot be reused, which is how a weaker internal path is
    /// born. the predecessor platform's <c>ManagedFileWriter</c> kept ONE storage path for its gated and ungated commands; this is that
    /// shape for Stomme's three controller paths now and the booked managed-file port later.
    /// </para>
    /// <para>
    /// <b>Why staging.</b> Refutations 10 and the 2026-09-15 correction: the pre-guard unzip extracted straight into the
    /// folder with <c>overwrite: true</c>, the upload merged straight into it after deleting the existing file, and a
    /// later capacity refusal then deleted that path AND the existing <c>File</c> row - silent destruction of a file the
    /// user already had. Here nothing touches the folder until the move, the move replaces a same-named file in one
    /// step (<c>File.Move(..., overwrite: true)</c>), and the staging directory is deleted in a <c>finally</c>.
    /// </para>
    /// <para>
    /// <b>Why counting.</b> A zip entry's declared size is a header field. Measured on .NET 10: a STORED entry whose
    /// headers claim 100 bytes still yields every byte it holds (a deflated one is truncated at the declared size). So
    /// the caps are enforced on the bytes as they come out of the decompressor, never on <c>entry.Length</c>. The ratio
    /// uses the header's compressed length, which can also lie - the size and total caps are the real bound; the ratio
    /// only refuses earlier.
    /// </para>
    /// <para>
    /// <b>The scanner fails closed.</b> A scanner that throws refuses the file (<see cref="FileIngestionRefusalReason.ScanFailed"/>).
    /// </para>
    /// </remarks>
    public sealed class FileIngestionGuard : IFileIngestionGuard
    {
        private const int BufferSize = 81920;

        private static readonly Regex PartNumbers = new(@"\.part_(\d+)_(\d+)$", RegexOptions.CultureInvariant);

        private readonly IWebHostEnvironment _environment;
        private readonly IConfigurationRoot _configuration;
        private readonly ISettingRepository _settings;
        private readonly IFileRepository _files;
        private readonly IFileScanner _scanner;
        private readonly FileDownloadHttpClient _http;
        private FileIngestionOptions? _options;

        public FileIngestionGuard(IWebHostEnvironment environment, IConfigurationRoot configuration, ISettingRepository settings, IFileRepository files, IFileScanner scanner, FileDownloadHttpClient http)
        {
            _environment = environment;
            _configuration = configuration;
            _settings = settings;
            _files = files;
            _scanner = scanner;
            _http = http;
        }

        public FileIngestionOptions Options => _options ??= FileIngestionOptions.FromConfiguration(_configuration);

        public async Task<bool> IsAllowedFileNameAsync(Alias alias, string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || !fileName.IsPathOrFileValid())
            {
                return false;
            }

            var uploadable = (await _settings.GetSettingAsync(EntityNames.Site, alias.SiteId, "UploadableFiles"))?.SettingValue;
            uploadable = string.IsNullOrEmpty(uploadable) ? Constants.UploadableFiles : uploadable;
            return uploadable.Split(',').Contains(Path.GetExtension(fileName).ToLower().Replace(".", ""));
        }

        public async Task<FileIngestionResult> AcceptChunkAsync(FileIngestionTarget target, string fileName, int partNumber, int totalParts, Stream content, long length, CancellationToken cancellationToken)
        {
            if (!await IsAllowedFileNameAsync(target.Alias, fileName))
            {
                return FileIngestionResult.Refused(FileIngestionRefusalReason.Extension, $"The file name '{fileName}' is invalid or its extension is not uploadable.");
            }

            if (partNumber < 1 || totalParts < 1 || partNumber > totalParts)
            {
                return FileIngestionResult.Refused(FileIngestionRefusalReason.InvalidChunk, $"Part {partNumber} of {totalParts} is not a valid chunk.");
            }

            var max = Options.MaxUploadBytes;
            if (length > max)
            {
                return TooLarge(fileName, length, max);
            }

            // Every part but the last is full size, so a non-final part of length L declares at least
            // (totalParts - 1) * L + 1 bytes: refused on the FIRST part, not after the disk has filled.
            if (partNumber < totalParts && (totalParts - 1L) * length + 1 > max)
            {
                return FileIngestionResult.Refused(FileIngestionRefusalReason.Size,
                    $"'{fileName}' declares {totalParts} parts of {length} bytes, at least {(totalParts - 1L) * length + 1} bytes, over the {max}-byte upload cap.");
            }

            var stagingRoot = StagingRoot(target.Alias);
            await SweepQuietlyAsync(stagingRoot);

            var chunkDirectory = FileIngestionPaths.ChunkDirectory(stagingRoot, target.Directory);
            Directory.CreateDirectory(chunkDirectory);

            // a set with another total is a restarted upload's leftovers
            foreach (var stale in PartsOf(chunkDirectory, fileName).Where(part => part.Total != totalParts))
            {
                TryDeleteFile(stale.Path);
            }

            var partPath = Path.Combine(chunkDirectory, $"{fileName}.part_{partNumber:000}_{totalParts:000}");
            var already = PartsOf(chunkDirectory, fileName)
                .Where(part => part.Total == totalParts && !string.Equals(part.Path, partPath, StringComparison.Ordinal))
                .Sum(part => new FileInfo(part.Path).Length);
            if (already + length > max)
            {
                DeleteSet(chunkDirectory, fileName);
                return TooLarge(fileName, already + length, max);
            }

            try
            {
                await using (var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                {
                    await CopyCappedAsync(content, output, max - already, cancellationToken);
                }
            }
            catch (CapExceededException)
            {
                DeleteSet(chunkDirectory, fileName);
                return TooLarge(fileName, max + 1, max);
            }

            var set = PartsOf(chunkDirectory, fileName).Where(part => part.Total == totalParts).OrderBy(part => part.Number).ToList();
            if (set.Count < totalParts)
            {
                return FileIngestionResult.Pending();
            }

            var ingest = NewIngestDirectory(stagingRoot);
            try
            {
                try
                {
                    await using (var merged = new FileStream(Path.Combine(ingest, fileName), FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                    {
                        var budget = max;
                        foreach (var part in set)
                        {
                            await using var input = await OpenPartAsync(part.Path, cancellationToken);
                            budget -= await CopyCappedAsync(input, merged, budget, cancellationToken);
                        }
                    }
                }
                catch (CapExceededException)
                {
                    return TooLarge(fileName, max + 1, max);
                }
                finally
                {
                    DeleteSet(chunkDirectory, fileName);
                }

                return await CommitAsync(target, ingest, new[] { fileName }, Array.Empty<string>(), cancellationToken);
            }
            finally
            {
                TryDeleteDirectory(ingest);
            }
        }

        public async Task<FileIngestionResult> ExtractZipAsync(FileIngestionTarget target, string zipPath, CancellationToken cancellationToken)
        {
            var options = Options;
            var ingest = NewIngestDirectory(StagingRoot(target.Alias));
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                var entries = archive.Entries;
                if (entries.Count > options.UnzipMaxEntries)
                {
                    return FileIngestionResult.Refused(FileIngestionRefusalReason.EntryCount,
                        $"The archive holds {entries.Count} entries, over the {options.UnzipMaxEntries}-entry cap.");
                }

                var names = new List<string>();
                var skipped = new List<string>();
                long total = 0;
                var buffer = new byte[BufferSize];
                foreach (var entry in entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        continue; // a directory entry
                    }

                    if (!await IsAllowedFileNameAsync(target.Alias, entry.Name))
                    {
                        skipped.Add(entry.FullName);
                        continue;
                    }

                    // the header's sizes are NOT trusted: bytes are counted as they leave the decompressor
                    var compressed = Math.Max(entry.CompressedLength, 1);
                    long entryBytes = 0;
                    await using (var source = entry.Open())
                    await using (var output = new FileStream(Path.Combine(ingest, entry.Name), FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                    {
                        int read;
                        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                        {
                            entryBytes += read;
                            total += read;
                            if (entryBytes > options.MaxUploadBytes)
                            {
                                return FileIngestionResult.Refused(FileIngestionRefusalReason.Size,
                                    $"The entry '{entry.FullName}' decompresses past the {options.MaxUploadBytes}-byte file cap (its header declares {entry.Length}).");
                            }

                            if (total > options.UnzipMaxTotalBytes)
                            {
                                return FileIngestionResult.Refused(FileIngestionRefusalReason.TotalSize,
                                    $"The archive decompresses past the {options.UnzipMaxTotalBytes}-byte total cap.");
                            }

                            if (entryBytes > options.UnzipRatioFloorBytes && entryBytes > compressed * options.UnzipMaxEntryRatio)
                            {
                                return FileIngestionResult.Refused(FileIngestionRefusalReason.Ratio,
                                    $"The entry '{entry.FullName}' expands more than {options.UnzipMaxEntryRatio} times its compressed size ({compressed} bytes).");
                            }

                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        }
                    }

                    if (!names.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(entry.Name);
                    }
                }

                return names.Count == 0
                    ? FileIngestionResult.Accepted(Array.Empty<string>(), skipped)
                    : await CommitAsync(target, ingest, names, skipped, cancellationToken);
            }
            catch (InvalidDataException ex)
            {
                return FileIngestionResult.Refused(FileIngestionRefusalReason.Corrupt, "The archive is not a readable zip file: " + ex.Message);
            }
            finally
            {
                TryDeleteDirectory(ingest);
            }
        }

        public async Task<FileIngestionResult> DownloadAsync(FileIngestionTarget target, string url, string fileName, CancellationToken cancellationToken)
        {
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(url, out var uri) || !OutboundConnectGuard.IsPermittedScheme(uri))
            {
                return FileIngestionResult.Refused(FileIngestionRefusalReason.Destination, "Only an absolute http or https URL can be downloaded.");
            }

            if (!await IsAllowedFileNameAsync(target.Alias, fileName))
            {
                return FileIngestionResult.Refused(FileIngestionRefusalReason.Extension, $"The file name '{fileName}' is invalid or its extension is not uploadable.");
            }

            var options = Options;
            var ingest = NewIngestDirectory(StagingRoot(target.Alias));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Options.Set(OutboundConnectGuard.AllowedHostsOption, (IReadOnlyCollection<string>)options.UploadByUrlAllowedHosts);

                HttpResponseMessage response;
                try
                {
                    response = await _http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                }
                catch (Exception ex) when (OutboundConnectGuard.IsDenial(ex))
                {
                    return FileIngestionResult.Refused(FileIngestionRefusalReason.Destination, InnermostMessage(ex));
                }
                catch (HttpRequestException ex)
                {
                    return FileIngestionResult.Refused(FileIngestionRefusalReason.Unreachable, $"'{uri.Host}' could not be reached: {InnermostMessage(ex)}");
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return FileIngestionResult.Refused(FileIngestionRefusalReason.Unreachable, $"'{uri.Host}' did not answer in time.");
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        // redirects are not followed, so a 3xx lands here too
                        return FileIngestionResult.Refused(FileIngestionRefusalReason.Unreachable, $"'{uri.Host}' answered {(int)response.StatusCode}.");
                    }

                    if (response.Content.Headers.ContentLength > options.MaxUploadBytes)
                    {
                        return TooLarge(fileName, response.Content.Headers.ContentLength.Value, options.MaxUploadBytes);
                    }

                    try
                    {
                        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                        await using var output = new FileStream(Path.Combine(ingest, fileName), FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
                        await CopyCappedAsync(source, output, options.MaxUploadBytes, cancellationToken);
                    }
                    catch (CapExceededException)
                    {
                        return TooLarge(fileName, options.MaxUploadBytes + 1, options.MaxUploadBytes);
                    }
                }

                return await CommitAsync(target, ingest, new[] { fileName }, Array.Empty<string>(), cancellationToken);
            }
            finally
            {
                TryDeleteDirectory(ingest);
            }
        }

        public async Task<bool> ExceedsCapacityAsync(Folder folder, long incomingBytes, IReadOnlyCollection<string> replacing)
        {
            if (folder.Capacity == 0)
            {
                return false;
            }

            var existing = (await _files.GetFilesAsync(folder.FolderId, false))
                .Where(file => !replacing.Contains(file.Name, StringComparer.OrdinalIgnoreCase))
                .Sum(file => (long)file.Size);
            return (existing + incomingBytes) / 1000000 >= folder.Capacity;
        }

        /// <summary>
        /// The tail every path shares, on files already staged: size, scan, capacity - then, and only then, the move
        /// into the folder.
        /// </summary>
        private async Task<FileIngestionResult> CommitAsync(FileIngestionTarget target, string ingest, IReadOnlyList<string> names, IReadOnlyList<string> skipped, CancellationToken cancellationToken)
        {
            var max = Options.MaxUploadBytes;
            long incoming = 0;
            foreach (var name in names)
            {
                var staged = Path.Combine(ingest, name);
                var length = new FileInfo(staged).Length;
                if (length > max)
                {
                    return TooLarge(name, length, max);
                }

                incoming += length;

                FileScanResult scan;
                try
                {
                    await using var content = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
                    scan = await _scanner.ScanAsync(content, name, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return FileIngestionResult.Refused(FileIngestionRefusalReason.ScanFailed, $"The scanner failed on '{name}', so it was refused: {ex.Message}");
                }

                if (scan == null || !scan.IsClean)
                {
                    return FileIngestionResult.Refused(FileIngestionRefusalReason.Threat, $"The scanner refused '{name}': {scan?.ThreatName ?? "no verdict"}.");
                }
            }

            if (target.Folder != null && await ExceedsCapacityAsync(target.Folder, incoming, names.ToArray()))
            {
                return FileIngestionResult.Refused(FileIngestionRefusalReason.Capacity,
                    $"Adding {incoming} bytes would take the folder '{target.Folder.Name}' to or over its {target.Folder.Capacity} MB capacity.");
            }

            Directory.CreateDirectory(target.Directory);
            foreach (var name in names)
            {
                var destination = Path.Combine(target.Directory, name);
                DeleteThumbnails(target.Directory, name);
                File.Move(Path.Combine(ingest, name), destination, overwrite: true);
            }

            return FileIngestionResult.Accepted(names, skipped);
        }

        private string StagingRoot(Alias alias) => FileIngestionPaths.StagingRoot(_environment.ContentRootPath, alias.TenantId, alias.SiteId);

        private async Task SweepQuietlyAsync(string stagingRoot)
        {
            try
            {
                await ChunkOrphanSweeper.SweepOrphanedPartsAsync(stagingRoot, Options.ChunkOrphanWindow, DateTime.UtcNow);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // opportunistic - the purge job sweeps daily
            }
        }

        private static string NewIngestDirectory(string stagingRoot)
        {
            var directory = FileIngestionPaths.NewIngestDirectory(stagingRoot);
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static FileIngestionResult TooLarge(string name, long bytes, long max) =>
            FileIngestionResult.Refused(FileIngestionRefusalReason.Size, $"'{name}' is {bytes} bytes or more, over the {max}-byte upload cap.");

        private static IEnumerable<(string Path, int Number, int Total)> PartsOf(string chunkDirectory, string fileName)
        {
            var prefix = fileName + ".part_";
            foreach (var path in Directory.GetFiles(chunkDirectory))
            {
                var name = Path.GetFileName(path);
                if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length <= prefix.Length)
                {
                    continue;
                }

                var match = PartNumbers.Match(name);
                if (match.Success && match.Index == fileName.Length
                    && int.TryParse(match.Groups[1].Value, out var number) && int.TryParse(match.Groups[2].Value, out var total))
                {
                    yield return (path, number, total);
                }
            }
        }

        private static void DeleteSet(string chunkDirectory, string fileName)
        {
            foreach (var part in PartsOf(chunkDirectory, fileName).ToList())
            {
                TryDeleteFile(part.Path);
            }
        }

        /// <summary>Removes the image thumbnails of a file about to be replaced - <c>name.WxH.png</c>, the shape <c>GetImage</c> writes.</summary>
        private static void DeleteThumbnails(string directory, string fileName)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var thumbnail = new Regex("^" + Regex.Escape(Path.GetFileNameWithoutExtension(fileName)) + @"\.\d+x\d+\.png$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            foreach (var path in Directory.GetFiles(directory).Where(path => thumbnail.IsMatch(Path.GetFileName(path))))
            {
                TryDeleteFile(path);
            }
        }

        private static async Task<FileStream> OpenPartAsync(string path, CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
                }
                catch (IOException) when (attempt < 5)
                {
                    await Task.Delay(200 * attempt, cancellationToken); // a part still being written by another request
                }
            }
        }

        private static async Task<long> CopyCappedAsync(Stream source, Stream destination, long cap, CancellationToken cancellationToken)
        {
            var buffer = new byte[BufferSize];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > cap)
                {
                    throw new CapExceededException();
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            return total;
        }

        private static string InnermostMessage(Exception exception)
        {
            var current = exception;
            while (current.InnerException != null)
            {
                current = current.InnerException;
            }

            return current.Message;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the orphan sweep gets it
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // the orphan sweep gets it
            }
        }

        private sealed class CapExceededException : Exception
        {
        }
    }
}