// Compiled into every test project by the root Directory.Build.targets, beside the machine-wide test-host throttle.
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Stomme.TestInfrastructure
{
    /// <summary>
    /// BL-156: what the suites leave on disk, removed when a test host starts - measured 2026-09-23 after a full gate:
    /// <list type="bullet">
    /// <item>13,787 RavenDB topology-cache files (<c>{database}.{hash}.raven-database-topology</c>) across five test
    /// <c>bin</c> folders - the client writes one per scratch database, and nothing ever removed one;</item>
    /// <item>about 600 <c>%TEMP%\stomme-*</c> scratch folders going back three days - installs a failed or killed run
    /// never tore down, and children of the four fixed-name composition roots, which one gate adds to every time.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run by <see cref="MachineWideTestHostLock"/> only once it HOLDS the machine-wide lock, so no other test host is
    /// live. The <c>%TEMP%</c> sweep still removes only entries OLDER than <see cref="StaleAfter"/>: a gate runs its
    /// suites as three separate test hosts, and the second must not take the first one's folders while it could still
    /// be read. With the throttle off (<c>STOMME_TEST_PARALLEL=1</c>, or <c>CI</c>) nothing is swept.
    /// </para>
    /// <para>
    /// Kept: <c>stomme-gate</c> (the gate's own logs) and <c>stomme-test-lock.log</c>. Everything else named
    /// <c>stomme-*</c> in <c>%TEMP%</c> is test scratch - the fixtures' own prefix - so a new suite's leftovers are
    /// covered without being listed here.
    /// </para>
    /// <para>
    /// Kept too, however old: the throwaway test servers' folders, <c>stomme-{name}-test</c> (BL-229 - MySQL, Redis,
    /// MongoDB, FTP, SFTP, QuestDB, CrateDB, RabbitMQ: <c>scripts/*-test-server.ps1</c>). Each holds the password of a
    /// container that outlives a day of runs; swept at six hours, the live tests lost it mid-gate while its server still
    /// ran (measured 2026-10-17: the MongoDB folder gone during the full gate, MySQL's and Redis's earlier, their servers
    /// made again at the gate's start).
    /// </para>
    /// </remarks>
    internal static class TestScratchSweep
    {
        internal const string TopologyPattern = "*.raven-database-topology";

        internal const string ScratchPrefix = "stomme-";

        internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

        internal static readonly IReadOnlyCollection<string> Keep = new[] { "stomme-gate", "stomme-test-lock.log" };

        // A throwaway test server's folder: stomme-mysql-test, stomme-sftp-test - one word between, no run's token.
        internal static readonly Regex TestServer = new("^stomme-[a-z0-9]+-test$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>Deletes the topology-cache files in <paramref name="directory"/> (not below it). Returns how many.</summary>
        internal static int Topology(string directory)
        {
            var removed = 0;
            foreach (var file in Enumerate(() => Directory.EnumerateFiles(directory, TopologyPattern, SearchOption.TopDirectoryOnly)))
            {
                removed += TryDelete(() => File.Delete(file));
            }

            return removed;
        }

        /// <summary>
        /// Deletes <c>stomme-*</c> entries - folders and files - directly under <paramref name="root"/> that were last
        /// written before <paramref name="nowUtc"/> minus <see cref="StaleAfter"/>, except <see cref="Keep"/>. Returns how many.
        /// </summary>
        internal static int Scratch(string root, DateTime nowUtc)
        {
            var cutoff = nowUtc - StaleAfter;
            var removed = 0;
            foreach (var path in Enumerate(() => Directory.EnumerateFileSystemEntries(root, ScratchPrefix + "*", SearchOption.TopDirectoryOnly)))
            {
                var name = Path.GetFileName(path);
                if (Keep.Contains(name, StringComparer.OrdinalIgnoreCase) || TestServer.IsMatch(name))
                {
                    continue;
                }

                if (Directory.Exists(path))
                {
                    if (Directory.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        removed += TryDelete(() => Directory.Delete(path, recursive: true));
                    }
                }
                else if (File.GetLastWriteTimeUtc(path) < cutoff)
                {
                    removed += TryDelete(() => File.Delete(path));
                }
            }

            return removed;
        }

        private static IEnumerable<string> Enumerate(Func<IEnumerable<string>> list)
        {
            try
            {
                return list().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }

        // One entry in use, locked or already gone is not worth failing a test host over.
        private static int TryDelete(Action delete)
        {
            try
            {
                delete();
                return 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }
}
