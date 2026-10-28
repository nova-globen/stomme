#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// Removes the leftovers of ingestions that never finished (BL-100 F1, absorbing BL-114): chunk parts of an upload
    /// whose remaining parts never arrived, and <c>ingest-*</c> staging directories of a request that died between
    /// staging and its <c>finally</c> (a killed process). Before the guard, parts were written straight into the
    /// visible folder and nothing ever removed an incomplete set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A part is matched by NAME SHAPE, <c>*.part_NNN_TTT</c> (digits only), never by the <c>*.part_*</c> wildcard
    /// alone: a user's <c>notes.part_one.txt</c> is not a part. Age is the file's last write time, compared with
    /// <c>Files:Chunks:OrphanMinutes</c> (60 by default) - a live upload writes a part at least once a chunk, so a set
    /// inside the window is never touched.
    /// </para>
    /// <para>
    /// Two callers: the guard, opportunistically, over the site's staging tree on every chunk arrival; and
    /// <see cref="PurgeJob"/>, daily per site, over the staging tree AND the site's two content trees (private and
    /// public), where the pre-guard controller left its parts.
    /// </para>
    /// </remarks>
    public static class ChunkOrphanSweeper
    {
        private static readonly Regex PartName = new(@"\.part_\d{3,}_\d{3,}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>True when <paramref name="fileName"/> has the <c>name.ext.part_NNN_TTT</c> shape of a chunk part.</summary>
        public static bool IsPartName(string fileName) => PartName.IsMatch(fileName);

        /// <summary>
        /// Sweeps a STAGING tree: aged <c>ingest-*</c> directories directly under <paramref name="root"/>, and aged
        /// chunk parts anywhere beneath it. Returns the number of parts and staging directories removed.
        /// </summary>
        public static Task<int> SweepOrphanedPartsAsync(string root, TimeSpan olderThan, DateTime utcNow) =>
            Task.FromResult(Sweep(root, olderThan, utcNow, includeStagingDirectories: true));

        private static int Sweep(string root, TimeSpan olderThan, DateTime utcNow, bool includeStagingDirectories)
        {
            var removed = 0;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return removed;
            }

            var cutoff = utcNow - olderThan;

            // Top level only, and never in a content tree: a user's folder may be called "ingest-anything".
            foreach (var directory in includeStagingDirectories ? SafeEnumerateDirectories(root, FileIngestionPaths.IngestDirectoryPrefix + "*", recurse: false) : Array.Empty<string>())
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    {
                        Directory.Delete(directory, recursive: true);
                        removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // in use or already gone - the next sweep gets it
                }
            }

            foreach (var file in SafeEnumerateFiles(root, "*.part_*").Where(path => IsPartName(Path.GetFileName(path))))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // in use or already gone - the next sweep gets it
                }
            }

            var chunks = Path.Combine(root, FileIngestionPaths.ChunksDirectoryName);
            if (includeStagingDirectories && Directory.Exists(chunks))
            {
                foreach (var directory in SafeEnumerateDirectories(chunks, "*", recurse: false))
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(directory).Any() && Directory.GetLastWriteTimeUtc(directory) < cutoff)
                        {
                            Directory.Delete(directory);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // a part arrived meanwhile - leave it
                    }
                }
            }

            return removed;
        }

        /// <summary>
        /// Sweeps one site: its staging tree and both content trees (<c>{contentRoot}/Content/Tenants/{t}/Sites/{s}</c>
        /// and <c>{webRoot}/Content/Tenants/{t}/Sites/{s}</c>).
        /// </summary>
        public static async Task<int> SweepSiteAsync(string contentRoot, string webRoot, int tenantId, int siteId, TimeSpan olderThan, DateTime utcNow)
        {
            var removed = await SweepOrphanedPartsAsync(FileIngestionPaths.StagingRoot(contentRoot, tenantId, siteId), olderThan, utcNow);
            // the content trees hold user folders: chunk parts only, never directories
            removed += Sweep(SiteContentRoot(contentRoot, tenantId, siteId), olderThan, utcNow, includeStagingDirectories: false);
            if (!string.IsNullOrEmpty(webRoot))
            {
                removed += Sweep(SiteContentRoot(webRoot, tenantId, siteId), olderThan, utcNow, includeStagingDirectories: false);
            }

            return removed;
        }

        private static string SiteContentRoot(string root, int tenantId, int siteId) =>
            Path.Combine(root, "Content", "Tenants", tenantId.ToString(), "Sites", siteId.ToString());

        private static string[] SafeEnumerateFiles(string root, string pattern)
        {
            try
            {
                return Directory.GetFiles(root, pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        private static string[] SafeEnumerateDirectories(string root, string pattern, bool recurse)
        {
            try
            {
                return Directory.GetDirectories(root, pattern, new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }
    }
}