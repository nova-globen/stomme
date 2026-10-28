#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Backup;
using Microsoft.AspNetCore.Hosting;

namespace Stomme.Infrastructure.Backup.Participants
{
    /// <summary>
    /// The tenants' files (BL-81, D106 item 3): <c>Content/Tenants/**</c> under the content root (private, served through the
    /// file controller) AND under the web root (public). Upload staging and logs are not carried.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Part.</b> A sequence of <c>[root u8][path length i32][path, '/'-separated UTF-8][length i64][bytes]</c> - binary,
    /// streamed; no file is held in memory.
    /// </para>
    /// <para>
    /// <b>Replace.</b> Each root is written into a staging folder BESIDE the live tree, and only when every file of the part
    /// has been written are the trees swapped (live renamed aside, staged renamed in, the old tree deleted). A part that
    /// fails half-way leaves the live trees exactly as they were. A file absent from the snapshot is absent afterwards.
    /// </para>
    /// <para>
    /// <b>Paths (D106 refutation 16's sibling, zip-slip).</b> A path is relative, '/'-separated, without an empty, '.' or
    /// '..' segment, a drive or a backslash - checked on validation, and again on replace, where the resolved path must lie
    /// inside the staging folder.
    /// </para>
    /// </remarks>
    public sealed class TenantContentFilesBackupParticipant(IWebHostEnvironment environment) : IBackupParticipant
    {
        public const string PartName = "content:tenants";

        private const byte ContentRoot = 1;
        private const byte WebRoot = 2;

        public string Name => PartName;

        /// <summary>The files' record layout, not a type's shape (BL-170): files have none, so this is bumped by hand when the
        /// part's own record format changes.</summary>
        public string SchemaVersion => "1";

        /// <summary>After the databases: files are referred to by rows, never the other way round.</summary>
        public int RestoreOrder => 300;

        public async Task ExportAsync(IBackupPartWriter writer, BackupScope scope, CancellationToken cancellationToken)
        {
            foreach (var (root, tree) in Trees())
            {
                if (!Directory.Exists(tree))
                {
                    continue;
                }

                var files = Directory.EnumerateFiles(tree, "*", SearchOption.AllDirectories)
                    .Select(file => (File: file, Relative: Path.GetRelativePath(tree, file).Replace(Path.DirectorySeparatorChar, '/')))
                    .OrderBy(entry => entry.Relative, StringComparer.Ordinal);
                foreach (var (file, relative) in files)
                {
                    await using var content = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
                    await FileTreePart.WriteHeaderAsync(writer.Content, root, relative, content.Length, cancellationToken);
                    await content.CopyToAsync(writer.Content, cancellationToken);
                }
            }
        }

        public async Task<BackupPartValidation> ValidateAsync(IBackupPartReader reader, BackupScope scope, CancellationToken cancellationToken)
        {
            var problems = new List<string>();
            var seen = new HashSet<(byte, string)>();
            await using var stream = reader.OpenRead();
            try
            {
                while (await FileTreePart.ReadHeaderAsync(stream, PartName, cancellationToken) is { } entry)
                {
                    if (PathProblem(entry.Root, entry.Path) is { } problem)
                    {
                        problems.Add(problem);
                    }
                    else if (!seen.Add((entry.Root, entry.Path)))
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
            var stamp = Guid.NewGuid().ToString("N");
            var trees = Trees().ToDictionary(entry => entry.Root, entry => entry.Tree);
            var staged = trees.ToDictionary(entry => entry.Key, entry => entry.Value + ".restoring-" + stamp);
            var written = trees.Keys.ToDictionary(root => root, _ => 0);
            // BL-171: the tree is Tenants/{tenantId}/..., so a partial restore writes and swaps the selected tenants'
            // folders only; every other tenant's folder stays as it is.
            var partial = scope.IsPartial ? scope.TenantIds.Select(id => id.ToString()).ToHashSet(StringComparer.Ordinal) : null;
            try
            {
                foreach (var folder in staged.Values)
                {
                    Directory.CreateDirectory(folder);
                }

                await using (var stream = reader.OpenRead())
                {
                    while (await FileTreePart.ReadHeaderAsync(stream, PartName, cancellationToken) is { } entry)
                    {
                        if (PathProblem(entry.Root, entry.Path) is { } problem || !staged.TryGetValue(entry.Root, out var folder))
                        {
                            throw new InvalidDataException($"{PartName}: refused - {PathProblem(entry.Root, entry.Path) ?? "a root this host does not have"}.");
                        }

                        if (partial is not null && !partial.Contains(entry.Path.Split('/')[0]))
                        {
                            await FileTreePart.SkipAsync(stream, entry.Length, PartName, cancellationToken);
                            continue;
                        }

                        var target = FileTreePart.Target(folder, entry.Path, PartName);

                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        await using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
                        {
                            await FileTreePart.CopyExactlyAsync(stream, file, entry.Length, PartName, cancellationToken);
                        }

                        written[entry.Root]++;
                    }
                }

                foreach (var (root, folder) in staged)
                {
                    var count = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count();
                    if (count != written[root])
                    {
                        throw new InvalidOperationException($"{PartName}: {count} files staged, {written[root]} written. The replace is not trusted.");
                    }
                }
            }
            catch
            {
                foreach (var folder in staged.Values.Where(Directory.Exists))
                {
                    Directory.Delete(folder, recursive: true);
                }

                throw;
            }

            // Every file is on disk: swap each tree in, and only then let the old one go.
            foreach (var (root, tree) in trees)
            {
                if (partial is not null)
                {
                    // The selected tenants' folders only: each swapped in under the tree, the rest of the tree untouched.
                    Directory.CreateDirectory(tree);
                    foreach (var tenant in partial)
                    {
                        var live = Path.Combine(tree, tenant);
                        var incoming = Path.Combine(staged[root], tenant);
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

                    Directory.Delete(staged[root], recursive: true);
                    continue;
                }

                var replaced = tree + ".replaced-" + stamp;
                if (Directory.Exists(tree))
                {
                    Directory.Move(tree, replaced);
                }

                Directory.Move(staged[root], tree);
                if (Directory.Exists(replaced))
                {
                    Directory.Delete(replaced, recursive: true);
                }
            }
        }

        /// <summary>The two trees this host has - the web root only when there is one.</summary>
        private IEnumerable<(byte Root, string Tree)> Trees()
        {
            yield return (ContentRoot, Path.Combine(environment.ContentRootPath, "Content", "Tenants"));
            if (!string.IsNullOrEmpty(environment.WebRootPath))
            {
                yield return (WebRoot, Path.Combine(environment.WebRootPath, "Content", "Tenants"));
            }
        }

        internal static string? PathProblem(byte root, string path) =>
            root is not (ContentRoot or WebRoot) ? $"an unknown root ({root})" : FileTreePart.PathProblem(path);
    }
}
