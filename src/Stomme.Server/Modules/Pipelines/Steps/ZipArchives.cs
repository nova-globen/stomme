#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// A file, or a folder's files, from the site's pipeline folder as a ZIP archive in it (BL-95's residue "ZIP file shapes",
    /// 2026-10-03). The entries are named relative to the folder, with forward slashes. What is written can be read back by
    /// <see cref="ExtractZipArchive"/>: no more entries than <c>Files:Unzip:MaxEntries</c> and no more bytes in all than
    /// <c>Files:Unzip:MaxTotalBytes</c>, the caps every unzip on this host has; no source file larger than
    /// <c>Pipelines:Files:MaxFileBytes</c>, and no archive larger either. It is written beside its destination and moved over
    /// it, so a failed write leaves the previous archive as it was.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Files", "Zip a file, or a folder's files, from the site's pipeline folder.", DisplayName = "Zip files")]
    public class CreateZipArchive : DataStep
    {
        [Input(UIHint = Stomme.Models.Pipelines.PipelineInputHints.PipelineFile, Description = "A file or a folder, relative to the site's pipeline folder. A folder's files are taken with their subfolders.", Order = 1)]
        public Input<string> Source { get; set; } = null!;

        [Input(UIHint = Stomme.Models.Pipelines.PipelineInputHints.PipelineFile, Description = "The archive's path, relative to the site's pipeline folder; it must end in .zip. Folders are created.", Order = 2)]
        public Input<string> Destination { get; set; } = null!;

        [Input(Description = "For a folder, the files to take, such as *.csv. Empty: every file.", Order = 3)]
        public Input<string?>? Pattern { get; set; }

        [Output(DisplayName = "Entry count")]
        public Output<int>? EntryCount { get; set; }

        [Output(DisplayName = "Full path", Description = "Where the archive was written.")]
        public Output<string>? FullPath { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var source = Required(context, Source, "Source");
            var destination = Required(context, Destination, "Destination");
            if (!string.Equals(Path.GetExtension(destination), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                await FailAsync(context, "FORMAT_UNSUPPORTED", $"'{destination}' does not end in .zip.").ConfigureAwait(false);
                return;
            }

            var sourceFull = PipelineFileSandbox.Resolve(context, source, out var refusal);
            var destinationFull = sourceFull is null ? null : PipelineFileSandbox.Resolve(context, destination, out refusal);
            if (sourceFull is null || destinationFull is null)
            {
                await FailAsync(context, "PATH_REFUSED", refusal!).ConfigureAwait(false);
                return;
            }

            List<(string Path, string Name)> files;
            if (File.Exists(sourceFull))
            {
                files = new() { (sourceFull, Path.GetFileName(sourceFull)) };
            }
            else if (Directory.Exists(sourceFull))
            {
                var pattern = Pattern?.GetOrDefault(context);
                files = Directory.EnumerateFiles(sourceFull, string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim(), SearchOption.AllDirectories)
                    .Where(path => !string.Equals(path, destinationFull, StringComparison.OrdinalIgnoreCase)) // never the archive into itself
                    .Order(StringComparer.Ordinal)
                    .Select(path => (path, Path.GetRelativePath(sourceFull, path).Replace('\\', '/')))
                    .ToList();
            }
            else
            {
                await FailAsync(context, "FILE_NOT_FOUND", $"No file or folder at '{source}'.").ConfigureAwait(false);
                return;
            }

            var max = MaxFileBytes(context);
            var caps = UnzipCaps(context);
            if (files.Count > caps.UnzipMaxEntries)
            {
                await FailAsync(context, "TOO_MANY_FILES", $"'{source}' holds {files.Count} files, over Files:Unzip:MaxEntries ({caps.UnzipMaxEntries}).").ConfigureAwait(false);
                return;
            }

            long total = 0;
            foreach (var (path, name) in files)
            {
                var length = new FileInfo(path).Length;
                if (length > max)
                {
                    await FailAsync(context, "FILE_TOO_LARGE", $"'{name}' is larger than Pipelines:Files:MaxFileBytes ({max}).").ConfigureAwait(false);
                    return;
                }

                total += length;
            }

            if (total > caps.UnzipMaxTotalBytes)
            {
                await FailAsync(context, "FILE_TOO_LARGE", $"'{source}' holds {total} bytes, over Files:Unzip:MaxTotalBytes ({caps.UnzipMaxTotalBytes}).").ConfigureAwait(false);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationFull)!);
            var partial = destinationFull + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var (path, name) in files)
                    {
                        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                        entry.LastWriteTime = File.GetLastWriteTime(path);
                        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                        await using var target = await entry.OpenAsync(context.CancellationToken).ConfigureAwait(false);
                        await input.CopyToAsync(target, context.CancellationToken).ConfigureAwait(false);
                    }
                }

                if (new FileInfo(partial).Length > max)
                {
                    File.Delete(partial);
                    await FailAsync(context, "FILE_TOO_LARGE", $"Writing '{destination}' would exceed Pipelines:Files:MaxFileBytes ({max}).").ConfigureAwait(false);
                    return;
                }

                File.Move(partial, destinationFull, overwrite: true);
            }
            catch (Exception) when (File.Exists(partial))
            {
                File.Delete(partial);
                throw;
            }

            context.Set(EntryCount, files.Count);
            context.Set(FullPath, destinationFull);
            await SucceedAsync(context, destination).ConfigureAwait(false);
        }

        internal static long MaxFileBytes(StepExecutionContext context) =>
            context.GetService<IOptions<PipelineStepOptions>>()?.Value.Files.MaxFileBytes ?? new FileStepOptions().MaxFileBytes;

        // The caps every unzip on this host has (the file manager's, BL-100 F1), from THIS host's configuration root.
        internal static FileIngestionOptions UnzipCaps(StepExecutionContext context) =>
            FileIngestionOptions.FromConfiguration(context.GetService<IConfigurationRoot>());
    }

    /// <summary>
    /// A ZIP archive in the site's pipeline folder extracted into a folder in it (the other half of the residue). The archive
    /// is not trusted: an entry whose name is rooted or climbs out of the destination refuses the archive (zip slip), and the
    /// caps - <c>Files:Unzip:MaxEntries</c>, <c>Files:Unzip:MaxTotalBytes</c>, <c>Files:Unzip:MaxEntryRatio</c> past
    /// <c>Files:Unzip:RatioFloorBytes</c>, and <c>Pipelines:Files:MaxFileBytes</c> per file - are enforced on the bytes as
    /// they leave the decompressor, never on what the entry headers declare (the file manager's rule, measured there). Every
    /// entry is extracted into a staging folder first; only when all of them have passed are the files moved into the
    /// destination, and a file already there is replaced only when <see cref="Overwrite"/> says so - checked for every file
    /// before the first move. The result is the extracted files as records, <c>path</c> and <c>bytes</c>, the paths relative
    /// to the site's pipeline folder. Empty folders in the archive are not reproduced.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Files", "Unzip an archive in the site's pipeline folder into a folder in it.", DisplayName = "Unzip files")]
    public class ExtractZipArchive : DataStep
    {
        [Input(UIHint = Stomme.Models.Pipelines.PipelineInputHints.PipelineFile, Description = "The archive's path, relative to the site's pipeline folder (.zip).", Order = 1)]
        public Input<string> Path { get; set; } = null!;

        [Input(UIHint = Stomme.Models.Pipelines.PipelineInputHints.PipelineFile, Description = "The folder to extract into, relative to the site's pipeline folder. Folders are created.", Order = 2)]
        public Input<string> Destination { get; set; } = null!;

        [Input(Description = "Replace files already in the destination. Off: the step fails before extracting anything when one is there.", DefaultValue = false, Order = 3)]
        public Input<bool> Overwrite { get; set; } = new(new Literal<bool>(false));

        [Output(DisplayName = "File count")]
        public Output<int>? FileCount { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var relative = Required(context, Path, "Path");
            var destination = Required(context, Destination, "Destination");
            if (!string.Equals(System.IO.Path.GetExtension(relative), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                await FailAsync(context, "FORMAT_UNSUPPORTED", $"'{relative}' is not a .zip file.").ConfigureAwait(false);
                return;
            }

            var archivePath = PipelineFileSandbox.Resolve(context, relative, out var refusal);
            var destinationFull = archivePath is null ? null : PipelineFileSandbox.Resolve(context, destination, out refusal);
            if (archivePath is null || destinationFull is null)
            {
                await FailAsync(context, "PATH_REFUSED", refusal!).ConfigureAwait(false);
                return;
            }

            if (!File.Exists(archivePath))
            {
                await FailAsync(context, "FILE_NOT_FOUND", $"No file at '{relative}'.").ConfigureAwait(false);
                return;
            }

            var max = CreateZipArchive.MaxFileBytes(context);
            if (new FileInfo(archivePath).Length > max)
            {
                await FailAsync(context, "FILE_TOO_LARGE", $"'{relative}' is larger than Pipelines:Files:MaxFileBytes ({max}).").ConfigureAwait(false);
                return;
            }

            var caps = CreateZipArchive.UnzipCaps(context);
            var siteRoot = PipelineFileSandbox.SiteRoot(context);
            var staging = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(destinationFull)!, "." + System.IO.Path.GetFileName(destinationFull) + "." + Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                List<(string Name, long Bytes)> extracted;
                try
                {
                    extracted = await StageAsync(context, archivePath, staging, max, caps).ConfigureAwait(false);
                }
                catch (ArchiveRefusedException ex)
                {
                    await FailAsync(context, ex.Code, ex.Message).ConfigureAwait(false);
                    return;
                }
                catch (InvalidDataException ex)
                {
                    await FailAsync(context, "ARCHIVE_CORRUPT", $"'{relative}' is not a readable zip file: {ex.Message}").ConfigureAwait(false);
                    return;
                }

                var overwrite = Overwrite.GetOrDefault(context);
                var existing = extracted.Select(file => file.Name).FirstOrDefault(name => File.Exists(Target(destinationFull, name)));
                if (existing is not null && !overwrite)
                {
                    await FailAsync(context, "FILE_EXISTS", $"'{existing}' is already in '{destination}'; nothing was extracted. Set Overwrite to replace it.").ConfigureAwait(false);
                    return;
                }

                foreach (var (name, _) in extracted)
                {
                    var target = Target(destinationFull, name);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                    File.Move(Target(staging, name), target, overwrite);
                }

                context.Set(FileCount, extracted.Count);
                var records = extracted.Select(file => new Dictionary<string, object>
                {
                    ["path"] = System.IO.Path.GetRelativePath(siteRoot, Target(destinationFull, file.Name)).Replace('\\', '/'),
                    ["bytes"] = file.Bytes,
                });
                await SucceedAsync(context, JsonSerializer.Serialize(records)).ConfigureAwait(false);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
        }

        // Every entry into the staging folder, counted as it decompresses; the archive is refused at the first entry or byte
        // past a cap, and an entry name that is not a plain relative path refuses it too.
        private static async Task<List<(string Name, long Bytes)>> StageAsync(StepExecutionContext context, string archivePath, string staging, long maxFileBytes, FileIngestionOptions caps)
        {
            await using var archive = await ZipFile.OpenReadAsync(archivePath, context.CancellationToken).ConfigureAwait(false);
            if (archive.Entries.Count > caps.UnzipMaxEntries)
            {
                throw new ArchiveRefusedException("TOO_MANY_FILES", $"The archive holds {archive.Entries.Count} entries, over Files:Unzip:MaxEntries ({caps.UnzipMaxEntries}).");
            }

            Directory.CreateDirectory(staging);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var extracted = new List<(string Name, long Bytes)>();
            var buffer = new byte[81920];
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    continue; // a folder entry: its files make it
                }

                var name = EntryName(entry.FullName);
                if (!names.Add(name))
                {
                    throw new ArchiveRefusedException("ENTRY_REFUSED", $"The archive names '{entry.FullName}' twice.");
                }

                var compressed = Math.Max(entry.CompressedLength, 1);
                long bytes = 0;
                var staged = Target(staging, name);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(staged)!);
                await using (var source = await entry.OpenAsync(context.CancellationToken).ConfigureAwait(false))
                await using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
                {
                    int read;
                    while ((read = await source.ReadAsync(buffer, context.CancellationToken).ConfigureAwait(false)) > 0)
                    {
                        bytes += read;
                        total += read;
                        if (bytes > maxFileBytes)
                        {
                            throw new ArchiveRefusedException("FILE_TOO_LARGE", $"The entry '{entry.FullName}' decompresses past Pipelines:Files:MaxFileBytes ({maxFileBytes}); its header declares {entry.Length}.");
                        }

                        if (total > caps.UnzipMaxTotalBytes)
                        {
                            throw new ArchiveRefusedException("FILE_TOO_LARGE", $"The archive decompresses past Files:Unzip:MaxTotalBytes ({caps.UnzipMaxTotalBytes}).");
                        }

                        if (bytes > caps.UnzipRatioFloorBytes && bytes > compressed * caps.UnzipMaxEntryRatio)
                        {
                            throw new ArchiveRefusedException("ARCHIVE_REFUSED", $"The entry '{entry.FullName}' expands more than {caps.UnzipMaxEntryRatio} times its compressed size ({compressed} bytes).");
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), context.CancellationToken).ConfigureAwait(false);
                    }
                }

                extracted.Add((name, bytes));
            }

            return extracted;
        }

        // An entry's name as a plain relative path with forward slashes, or the archive is refused: rooted, a drive, a UNC
        // path, an empty segment, '.' or '..' anywhere - the zip-slip class is refused, not repaired.
        internal static string EntryName(string fullName)
        {
            var name = fullName.Replace('\\', '/');
            var segments = name.Split('/');
            if (name.StartsWith('/') || name.Contains(':') || segments.Any(segment => segment.Length == 0 || segment == "." || segment == "..")
                || segments.Any(segment => segment.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0))
            {
                throw new ArchiveRefusedException("ENTRY_REFUSED", $"The archive's entry '{fullName}' is not a plain relative path; nothing was extracted.");
            }

            return name;
        }

        private static string Target(string folder, string entryName) =>
            System.IO.Path.Combine(new[] { folder }.Concat(entryName.Split('/')).ToArray());

        private sealed class ArchiveRefusedException(string code, string message) : Exception(message)
        {
            public string Code { get; } = code;
        }
    }
}
