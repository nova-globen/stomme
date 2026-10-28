#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Backup;
using Stomme.Infrastructure.Backup.Participants;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Persistence
{
    /// <summary>
    /// BL-233 - the site's pipeline folders in an installation's backup: what the file steps read and write
    /// (<c>Pipelines:Files:Root</c>, a folder per site), which no part carried - a restore kept the live folders' files, and a
    /// restore onto a new host had none, a CSV an author put in for a pipeline to read and a report a run wrote alike.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Part.</b> The content part's records (<see cref="FileTreePart"/>), one root, each path starting with its site's folder
    /// (<c>1-2/reports/q3.csv</c>). Each file is copied for exactly the length its header names, so a file a run changes while
    /// the backup reads it fails the part rather than corrupting it. The folder where uploads are written until whole
    /// (<c>.incoming</c>) is not carried, nor a replace's staging, nor a file loose at the root - the steps write in a site's
    /// folder only.
    /// </para>
    /// <para>
    /// <b>Replace.</b> As the content part: written into a staging folder beside the live root, swapped in only once every file
    /// is on disk; a part that fails half-way leaves the live folders as they were. A tenant-scoped restore (BL-171) swaps
    /// the restored tenants' site folders only (<c>{tenant}-{site}</c>); another tenant's, and the engine's default folder,
    /// stay as they are.
    /// </para>
    /// </remarks>
    public sealed class PipelineFilesBackupParticipant(IOptions<PipelineStepOptions> options) : IBackupParticipant
    {
        public const string PartName = "pipelines:files";

        private const byte FilesRoot = 1;

        public string Name => PartName;

        /// <summary>The record layout, bumped by hand when it changes - files have no type shape (BL-170).</summary>
        public string SchemaVersion => "1";

        /// <summary>After the engine's rows (600): a file is named by a pipeline, never the other way round.</summary>
        public int RestoreOrder => 650;

        private string Root => Path.GetFullPath(options.Value.Files.Root
            ?? throw new InvalidOperationException("Pipelines:Files:Root is not configured and the host gave the file steps no data directory."));

        public async Task ExportAsync(IBackupPartWriter writer, BackupScope scope, CancellationToken cancellationToken)
        {
            var root = Root;
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (var site in Sites(root).Where(site => InScope(scope, site)))
            {
                var files = Directory.EnumerateFiles(Path.Combine(root, site), "*", SearchOption.AllDirectories)
                    .Select(file => (File: file, Relative: Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')))
                    .OrderBy(entry => entry.Relative, StringComparer.Ordinal);
                foreach (var (file, relative) in files)
                {
                    await using var content = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
                    var length = content.Length;
                    await FileTreePart.WriteHeaderAsync(writer.Content, FilesRoot, relative, length, cancellationToken);
                    await FileTreePart.CopyExactlyAsync(content, writer.Content, length, PartName, cancellationToken);
                }
            }
        }

        public async Task<BackupPartValidation> ValidateAsync(IBackupPartReader reader, BackupScope scope, CancellationToken cancellationToken)
        {
            var problems = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            await using var stream = reader.OpenRead();
            try
            {
                while (await FileTreePart.ReadHeaderAsync(stream, PartName, cancellationToken) is { } entry)
                {
                    if (PathProblem(entry.Root, entry.Path) is { } problem)
                    {
                        problems.Add(problem);
                    }
                    else if (!seen.Add(entry.Path))
                    {
                        problems.Add($"'{entry.Path}' appears twice");
                    }

                    await FileTreePart.SkipAsync(stream, entry.Length, PartName, cancellationToken);
                    if (problems.Count >= 5)
                    {
                        break;
                    }
                }
            }
            catch (InvalidDataException ex)
            {
                problems.Add(ex.Message);
            }

            return problems.Count == 0 ? BackupPartValidation.Valid : BackupPartValidation.Invalid(problems.ToArray());
        }

        public async Task ReplaceAsync(IBackupPartReader reader, BackupScope scope, CancellationToken cancellationToken)
        {
            var root = Root;
            var stamp = Guid.NewGuid().ToString("N");
            var staged = root + ".restoring-" + stamp;
            var written = 0;
            try
            {
                Directory.CreateDirectory(staged);
                await using (var stream = reader.OpenRead())
                {
                    while (await FileTreePart.ReadHeaderAsync(stream, PartName, cancellationToken) is { } entry)
                    {
                        if (PathProblem(entry.Root, entry.Path) is { } problem)
                        {
                            throw new InvalidDataException($"{PartName}: refused - {problem}.");
                        }

                        if (!InScope(scope, entry.Path.Split('/')[0]))
                        {
                            await FileTreePart.SkipAsync(stream, entry.Length, PartName, cancellationToken);
                            continue;
                        }

                        var target = FileTreePart.Target(staged, entry.Path, PartName);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        await using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                        {
                            await FileTreePart.CopyExactlyAsync(stream, file, entry.Length, PartName, cancellationToken);
                        }

                        written++;
                    }
                }

                var count = Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories).Count();
                if (count != written)
                {
                    throw new InvalidOperationException($"{PartName}: {count} files staged, {written} written. The replace is not trusted.");
                }
            }
            catch
            {
                if (Directory.Exists(staged))
                {
                    Directory.Delete(staged, recursive: true);
                }

                throw;
            }

            // Every file is on disk: swap the site folders in, and only then let the old ones go.
            if (scope.IsPartial)
            {
                Directory.CreateDirectory(root);
                var sites = Sites(root).Concat(Sites(staged)).Distinct(StringComparer.Ordinal).Where(site => InScope(scope, site)).ToList();
                foreach (var site in sites)
                {
                    var live = Path.Combine(root, site);
                    var incoming = Path.Combine(staged, site);
                    var aside = live + ".replaced-" + stamp;
                    if (Directory.Exists(live))
                    {
                        Directory.Move(live, aside);
                    }

                    if (Directory.Exists(incoming))
                    {
                        Directory.Move(incoming, live);
                    }

                    if (Directory.Exists(aside))
                    {
                        Directory.Delete(aside, recursive: true);
                    }
                }

                Directory.Delete(staged, recursive: true);
                return;
            }

            var replaced = root + ".replaced-" + stamp;
            if (Directory.Exists(root))
            {
                Directory.Move(root, replaced);
            }

            Directory.Move(staged, root);
            if (Directory.Exists(replaced))
            {
                Directory.Delete(replaced, recursive: true);
            }
        }

        /// <summary>The site folders under a root, by name: not the upload staging, nor a replace's own folders.</summary>
        private static IEnumerable<string> Sites(string root) =>
            !Directory.Exists(root)
                ? Enumerable.Empty<string>()
                : Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>()
                    .Where(name => !name.StartsWith('.') && !name.Contains(".restoring-", StringComparison.Ordinal) && !name.Contains(".replaced-", StringComparison.Ordinal))
                    .OrderBy(name => name, StringComparer.Ordinal);

        // A site's folder belongs to the Stomme tenant its name says; the engine's default folder belongs to the installation,
        // so only a full restore touches it.
        private static bool InScope(BackupScope scope, string site) =>
            !scope.IsPartial || (PipelineFileSandbox.TenantOfSiteFolder(site, out var tenantId) && scope.TenantIds.Contains(tenantId));

        internal static string? PathProblem(byte root, string path)
        {
            if (root != FilesRoot)
            {
                return $"an unknown root ({root})";
            }

            if (FileTreePart.PathProblem(path) is { } problem)
            {
                return problem;
            }

            var site = path.Split('/')[0];
            return site.StartsWith('.') || !path.Contains('/') ? $"'{path}' is not a file in a site's folder" : null;
        }
    }
}
