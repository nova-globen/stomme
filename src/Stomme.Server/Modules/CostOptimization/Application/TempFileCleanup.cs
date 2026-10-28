#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.CostOptimization.Application
{
    /// <summary>
    /// The resource-cleanup settings (the predecessor platform's <c>CleanupOptions</c>), read from the HOST's configuration section
    /// <c>CostOptimization:Cleanup</c> (BL-162). Reversible by default: <see cref="DryRun"/> starts true (the job only
    /// reports what it would remove) and nothing newer than <see cref="SafetyWindowDays"/> is ever touched.
    /// </summary>
    /// <remarks>
    /// <b>CHANGED from the predecessor platform:</b> the temp-file job is OFF by default (<c>TempFiles:Enabled</c> false - BL-97 default 5,
    /// a job that deletes files is opt-in); the predecessor platform's <c>RecommendationMinBytes</c> is gone with the recommendation it
    /// gated (see <see cref="TempFileSweeper"/>); the predecessor platform's stale-cache settings are gone with the eviction job (see
    /// <see cref="CacheManagerModelResponseCache"/>).
    /// </remarks>
    public sealed class CleanupOptions
    {
        public const string SectionName = "CostOptimization:Cleanup";

        /// <summary>When true (the default) the job counts and reports what it would remove, deleting nothing.</summary>
        public bool DryRun { get; set; } = true;

        /// <summary>A file younger than this many days is never a candidate, dry run or live. Default 7; non-positive is zero.</summary>
        public int SafetyWindowDays { get; set; } = 7;

        public TempFileCleanupOptions TempFiles { get; set; } = new();

        public static CleanupOptions From(IConfiguration? configuration)
        {
            var options = new CleanupOptions();
            configuration?.GetSection(SectionName).Bind(options);
            return options;
        }
    }

    /// <summary>The temp-file sweep's settings: off by default, and inert until an operator names the directories to sweep.</summary>
    public sealed class TempFileCleanupOptions
    {
        public bool Enabled { get; set; }

        /// <summary>Absolute paths swept recursively. Empty (the default) sweeps nothing - no path is hard-coded.</summary>
        public IList<string> Directories { get; set; } = new List<string>();
    }

    /// <summary>What one sweep did: whether it ran, why not when it did not, and the files and bytes it counted or removed.</summary>
    public sealed record TempFileSweepResult(bool Ran, bool DryRun, int Count, long Bytes, string Report);

    /// <summary>
    /// the predecessor platform's temp-file sweep (<c>TempFileCleanupJob</c>'s body): the directories an operator configured, recursively; a
    /// file older than the safety window is a candidate, counted in a dry run and deleted in a live one; a locked or vanished
    /// file is left for the next run; an unreadable directory is skipped.
    /// </summary>
    /// <remarks>
    /// <b>CHANGED from the predecessor platform:</b> a dry run REPORTS the reclaimable space in the job's log instead of raising a Cleanup
    /// recommendation. The finding is the installation's - the swept directories are the host's - while a recommendation is
    /// a site's and is shown to that site's administrators; its evidence would carry the host's directory layout to every
    /// tenant. The job's log is the host's.
    /// </remarks>
    public sealed class TempFileSweeper(ILogger<TempFileSweeper> logger, TimeProvider clock)
    {
        public TempFileSweepResult Sweep(CleanupOptions cleanup, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(cleanup);
            if (!cleanup.TempFiles.Enabled)
            {
                return new TempFileSweepResult(false, cleanup.DryRun, 0, 0, $"Temp File Cleanup Is Disabled ({CleanupOptions.SectionName}:TempFiles:Enabled)");
            }

            var directories = cleanup.TempFiles.Directories
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Select(directory => directory.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (directories.Count == 0)
            {
                return new TempFileSweepResult(false, cleanup.DryRun, 0, 0, $"Temp File Cleanup: No Directories Configured ({CleanupOptions.SectionName}:TempFiles:Directories)");
            }

            var windowDays = Math.Max(cleanup.SafetyWindowDays, 0);
            var cutoffUtc = (clock.GetUtcNow() - TimeSpan.FromDays(windowDays)).UtcDateTime;
            var count = 0;
            var bytes = 0L;
            var lines = new List<string>();
            foreach (var directory in directories)
            {
                ct.ThrowIfCancellationRequested();
                if (!Directory.Exists(directory))
                {
                    logger.LogDebug("Temp-file cleanup directory {Directory} does not exist; skipping it.", directory);
                    continue;
                }

                var directoryCount = 0;
                var directoryBytes = 0L;
                foreach (var path in ListFiles(directory))
                {
                    ct.ThrowIfCancellationRequested();
                    var file = new FileInfo(path);
                    if (!file.Exists || file.LastWriteTimeUtc >= cutoffUtc)
                    {
                        continue; // inside the safety window (or already gone) - never a candidate
                    }

                    var length = file.Length;
                    if (cleanup.DryRun || TryDelete(file))
                    {
                        directoryCount++;
                        directoryBytes += length;
                    }
                }

                if (directoryCount > 0)
                {
                    lines.Add(string.Create(CultureInfo.InvariantCulture, $"{directory}: {directoryCount} file(s), {directoryBytes} bytes"));
                }

                count += directoryCount;
                bytes += directoryBytes;
            }

            var verb = cleanup.DryRun ? "Would Remove" : "Removed";
            var report = string.Create(CultureInfo.InvariantCulture, $"Temp File Cleanup {(cleanup.DryRun ? "(Dry Run) " : string.Empty)}{verb} {count} File(s), {bytes} Bytes Older Than {windowDays} Day(s)")
                + (lines.Count == 0 ? string.Empty : " - " + string.Join("; ", lines));
            logger.LogInformation("{Report}", report);
            return new TempFileSweepResult(true, cleanup.DryRun, count, bytes, report);
        }

        private string[] ListFiles(string directory)
        {
            try
            {
                return Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Temp-file cleanup could not enumerate {Directory}; skipping it this run.", directory);
                return Array.Empty<string>();
            }
        }

        private bool TryDelete(FileInfo file)
        {
            try
            {
                file.Delete();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort: a locked or already-removed file is left for the next run.
                logger.LogDebug(ex, "Temp-file cleanup could not delete {File}; it stays until the next run.", file.FullName);
                return false;
            }
        }
    }

    /// <summary>
    /// The daily temp-file cleanup (BL-97 increment 8, the predecessor platform's <c>TempFileCleanupJob</c>): gated at run time by the HOST's
    /// <c>CostOptimization:Cleanup:TempFiles:Enabled</c> (default off). The directories are the installation's, so the sweep
    /// runs in the MASTER tenant's pass only - the scheduler runs a job once per tenant, and every other tenant's pass says so
    /// and touches nothing.
    /// </summary>
    public class TempFileCleanupJob : HostedServiceBase
    {
        // JobType = "Stomme.Modules.CostOptimization.Application.TempFileCleanupJob, Stomme.Server"
        public TempFileCleanupJob(IServiceScopeFactory serviceScopeFactory) : base(serviceScopeFactory)
        {
            Name = "Cost Optimization Temp File Cleanup Job";
            Frequency = "d"; // day
            Interval = 1;
            IsEnabled = true; // gated at run time by CostOptimization:Cleanup:TempFiles:Enabled
        }

        public override async Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            var options = CleanupOptions.From(HostConfiguration.Resolve(provider));
            if (!options.TempFiles.Enabled)
            {
                return $"Temp File Cleanup Is Disabled ({CleanupOptions.SectionName}:TempFiles:Enabled)<br />";
            }

            var tenant = await provider.GetRequiredService<ITenantManager>().GetTenantAsync();
            if (tenant is null || !string.Equals(tenant.Name, TenantNames.Master, StringComparison.Ordinal))
            {
                return "Temp File Cleanup: The Installation's Directories Are Swept In The Master Tenant's Pass<br />";
            }

            return provider.GetRequiredService<TempFileSweeper>().Sweep(options).Report + "<br />";
        }
    }
}
