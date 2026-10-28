#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Stomme.Infrastructure;
using Stomme.Modules.CostOptimization.Application;
using Stomme.Tests.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Stomme.Tests.CostOptimization
{
    /// <summary>
    /// BL-97 increment 8 - the predecessor platform's <c>CleanupJobTests</c> (its seven temp-file cases) and the cleanup cases of
    /// <c>CostOptimizationRegistrationTests</c>, by name, over the sweep the daily job runs. CHANGED: the three
    /// recommendation cases - a dry run REPORTS the reclaimable space (the host's job log) and raises no recommendation, since
    /// its evidence would carry the host's directory layout to a site's administrators; and the job is off by default.
    /// the predecessor platform's four stale-cache cases are not ported: the eviction job has no equivalent over <c>ICacheManager</c>, which
    /// carries no tags, and a response-cache entry is retired by its TTL (<c>ModelResponseCachingTests</c>).
    /// </summary>
    public sealed class CleanupJobTests : IDisposable
    {
        private static readonly DateTime Now = new(2026, 7, 3, 4, 20, 0, DateTimeKind.Utc);

        private readonly string _root = Path.Combine(Path.GetTempPath(), $"stomme-costopt-cleanup-{Guid.NewGuid():N}");

        public CleanupJobTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        // --- The temp-file sweep ---

        [Fact]
        public void TempFiles_DryRun_CountsCandidatesWithoutDeletingAnything()
        {
            var expired = WriteFile("expired.tmp", ageDays: 8);
            var recent = WriteFile("recent.tmp", ageDays: 6);

            var result = Sweep(); // DryRun default true, SafetyWindowDays default 7

            Assert.Equal(1, result.Count); // only the file past the window is a candidate
            Assert.True(File.Exists(expired)); // dry run never deletes
            Assert.True(File.Exists(recent));
        }

        [Fact]
        public void TempFiles_LiveMode_RemovesOnlyFilesOlderThanTheSafetyWindow()
        {
            var expired = WriteFile("expired.tmp", ageDays: 8); // window + 1 day
            var recent = WriteFile("recent.tmp", ageDays: 6); // window - 1 day

            var result = Sweep(o => o.DryRun = false);

            Assert.Equal(1, result.Count);
            Assert.False(File.Exists(expired));
            Assert.True(File.Exists(recent)); // inside the safety window - never touched
        }

        [Fact]
        public void TempFiles_Disabled_SweepsNothing()
        {
            var expired = WriteFile("expired.tmp", ageDays: 30);

            var result = Sweep(o =>
            {
                o.DryRun = false;
                o.TempFiles.Enabled = false;
            });

            Assert.Equal((false, 0), (result.Ran, result.Count));
            Assert.True(File.Exists(expired));
        }

        [Fact]
        public void TempFiles_WithNoConfiguredOrMissingDirectories_NoOps()
        {
            var none = Sweep(configureDirectories: false);
            var missing = Sweep(o =>
            {
                o.DryRun = false;
                o.TempFiles.Directories = new List<string> { Path.Combine(_root, "does-not-exist") };
            });

            Assert.Equal((false, 0), (none.Ran, none.Count));
            Assert.Equal(0, missing.Count);
        }

        // CHANGED: the predecessor platform's TempFiles_DryRun_RaisesOneCleanupRecommendationOverTheThreshold - the finding is reported, not raised.
        [Fact]
        public void TempFiles_DryRun_ReportsTheReclaimableSpaceWithoutARecommendation()
        {
            WriteFile("expired.tmp", ageDays: 8, content: "reclaimable bytes");

            var result = Sweep();

            Assert.True(result.DryRun);
            Assert.Equal((1, 17L), (result.Count, result.Bytes));
            Assert.Contains("(Dry Run) Would Remove 1 File(s), 17 Bytes", result.Report);
            Assert.Contains(_root, result.Report); // the host's path, in the host's log - never in a site's recommendation
        }

        // CHANGED: the predecessor platform's TempFiles_DryRun_BelowTheThreshold_RaisesNothing - there is no threshold; a small finding is still counted.
        [Fact]
        public void TempFiles_DryRun_CountsASmallCandidateToo()
        {
            WriteFile("expired.tmp", ageDays: 8, content: "small");

            var result = Sweep();

            Assert.Equal((1, 5L), (result.Count, result.Bytes)); // still counted as a candidate
        }

        // CHANGED: the predecessor platform's TempFiles_LiveMode_RaisesNoRecommendation - live mode reports what it removed.
        [Fact]
        public void TempFiles_LiveMode_ReportsWhatItRemoved()
        {
            WriteFile("expired.tmp", ageDays: 8, content: "reclaimable bytes");

            var result = Sweep(o => o.DryRun = false);

            Assert.False(result.DryRun);
            Assert.Contains("Removed 1 File(s), 17 Bytes", result.Report);
            Assert.DoesNotContain("Would Remove", result.Report);
        }

        // --- The options (the predecessor platform's CostOptimizationRegistrationTests cleanup cases) ---

        // CHANGED: the section is CostOptimization:Cleanup in the HOST's configuration; the recommendation and stale-cache keys are gone.
        [Fact]
        public void AddCostOptimization_BindsCleanupOptionsFromConfiguration()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CostOptimization:Cleanup:DryRun"] = "false",
                ["CostOptimization:Cleanup:SafetyWindowDays"] = "14",
                ["CostOptimization:Cleanup:TempFiles:Enabled"] = "true",
                ["CostOptimization:Cleanup:TempFiles:Directories:0"] = "/var/stomme/spill",
            }).Build();

            var options = CleanupOptions.From(configuration);

            Assert.False(options.DryRun);
            Assert.Equal(14, options.SafetyWindowDays);
            Assert.True(options.TempFiles.Enabled);
            Assert.Equal(new[] { "/var/stomme/spill" }, options.TempFiles.Directories);
        }

        // CHANGED: the temp-file job is OFF by default (BL-97 default 5), where the predecessor platform's was on and dry-run.
        [Fact]
        public void CleanupOptions_DefaultToTheReversibleSafeMode()
        {
            var options = new CleanupOptions();

            Assert.True(options.DryRun); // nothing is deleted until an operator opts in
            Assert.Equal(7, options.SafetyWindowDays);
            Assert.False(options.TempFiles.Enabled);
            Assert.Empty(options.TempFiles.Directories); // no hard-coded sweep paths
        }

        // CHANGED: the predecessor platform's AddCostOptimization_RegistersTheAdvisorAndCleanupJobs - the jobs are HostedServiceBase types the
        // framework's assembly scan finds, so the case pins the two job types and their cadence.
        [Fact]
        public void AddCostOptimization_RegistersTheAdvisorAndCleanupJobs()
        {
            var jobs = typeof(TempFileCleanupJob).Assembly.GetTypes()
                .Where(type => type.Namespace == typeof(TempFileCleanupJob).Namespace && typeof(HostedServiceBase).IsAssignableFrom(type) && !type.IsAbstract)
                .Select(type => type.Name)
                .OrderBy(name => name, StringComparer.Ordinal);

            Assert.Equal(new[] { nameof(CostOptimizationAdvisorJob), nameof(TempFileCleanupJob) }, jobs);
        }

        private TempFileSweepResult Sweep(Action<CleanupOptions>? configure = null, bool configureDirectories = true)
        {
            var options = new CleanupOptions();
            options.TempFiles.Enabled = true;
            if (configureDirectories)
            {
                options.TempFiles.Directories = new List<string> { _root };
            }

            configure?.Invoke(options);
            return new TempFileSweeper(NullLogger<TempFileSweeper>.Instance, new ManualTimeProvider(Now)).Sweep(options);
        }

        private string WriteFile(string name, int ageDays, string content = "x")
        {
            var path = Path.Combine(_root, name);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, Now.AddDays(-ageDays));
            return path;
        }
    }
}
