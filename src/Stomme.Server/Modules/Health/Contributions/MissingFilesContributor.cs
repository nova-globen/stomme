#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Infrastructure;
using Stomme.Repository;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Health.Contributions
{
    /// <summary>
    /// ONE Site-scope probe, <c>missing-files</c>, naming the file rows of a site whose file is
    /// gone from disk - what an operator has to upload again. The seeded logo is put back when its file is missing,
    /// but a file a user uploaded and lost the same way (for example when storage lived in a container's own layer) cannot be put back
    /// while the database still lists it; without this, the first anyone hears of it is a broken image on a page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Degraded, never Unhealthy:</b> the site serves; what is missing is content, and the answer is a re-upload.
    /// </para>
    /// <para>
    /// <b>It does not walk the disk every minute.</b> The probe pass runs a Site-scope probe every tick, and checking every
    /// file of a large site each time is work for nothing, so a site's answer is kept for <see cref="Interval"/> (per
    /// tenant and site, in this process) and the walk stops at <see cref="Budget"/> - well inside the pass's probe
    /// timeout - saying how far it got. The next walk starts again from the first folder.
    /// </para>
    /// </remarks>
    public sealed class MissingFilesContributor(IFolderRepository folders, IFileRepository files, ITenantManager tenants, TimeProvider clock, ILogger<MissingFilesContributor> logger) : IHealthContributor
    {
        public const string Id = "files";

        public const string ProbeName = "missing-files";

        /// <summary>How long a site's answer is kept before its files are checked again.</summary>
        public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

        /// <summary>How long one walk may take before it stops and says how far it got.</summary>
        public static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

        /// <summary>How many missing files the answer names; the count covers the rest.</summary>
        public const int NamedLimit = 25;

        /// <summary>How many missing files the description names - the part the Health page shows.</summary>
        public const int DescribedLimit = 10;

        private static readonly ConcurrentDictionary<string, (DateTime CheckedUtc, HealthProbeResult Result)> Answers = new(StringComparer.Ordinal);

        public string ContributorId => Id;

        public IReadOnlyList<IHealthProbe> Probes => new IHealthProbe[] { new MissingFilesProbe(this) };

        public IReadOnlyList<HealthMetricDescriptor> Metrics { get; } = Array.Empty<HealthMetricDescriptor>();

        public IHealthMetricSource? SourceFor(string metricName) => null;

        /// <summary>Forgets every kept answer - for a test that changes a site's files between two checks.</summary>
        internal static void ForgetAnswers() => Answers.Clear();

        private async Task<HealthProbeResult> CheckAsync(HealthProbeContext context, CancellationToken cancellationToken)
        {
            var watch = Stopwatch.StartNew();
            string key;
            try
            {
                var alias = await tenants.GetAliasAsync().ConfigureAwait(false);
                key = $"{alias?.TenantId}:{context.SiteId}";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new HealthProbeResult(ProbeName, "Files", HealthState.Unhealthy, "The site's tenant could not be resolved: " + ex.GetType().Name, watch.Elapsed);
            }

            var now = clock.GetUtcNow().UtcDateTime;
            if (Answers.TryGetValue(key, out var kept) && now - kept.CheckedUtc < Interval)
            {
                return kept.Result;
            }

            var result = await WalkAsync(context.SiteId, watch, cancellationToken).ConfigureAwait(false);
            Answers[key] = (now, result);
            return result;
        }

        private async Task<HealthProbeResult> WalkAsync(int siteId, Stopwatch watch, CancellationToken cancellationToken)
        {
            var missing = new List<string>();
            var checkedCount = 0;
            var complete = true;
            try
            {
                foreach (var folder in (await folders.GetFoldersAsync(siteId).ConfigureAwait(false)).OrderBy(folder => folder.Path, StringComparer.Ordinal))
                {
                    if (watch.Elapsed > Budget)
                    {
                        complete = false;
                        break;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    var path = await folders.GetFolderPathAsync(folder).ConfigureAwait(false);
                    foreach (var file in await files.GetFilesAsync(folder.FolderId, false).ConfigureAwait(false))
                    {
                        checkedCount++;
                        if (!System.IO.File.Exists(Path.Combine(path, file.Name)))
                        {
                            missing.Add(folder.Path + file.Name);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("The files of site {SiteId} could not be checked: {ExceptionType}.", siteId, ex.GetType().FullName);
                return new HealthProbeResult(ProbeName, "Files", HealthState.Unhealthy, "The site's files could not be checked: " + ex.GetType().Name, watch.Elapsed);
            }

            var scope = complete ? $"{checkedCount} file(s) checked" : $"{checkedCount} file(s) checked before the {Budget.TotalSeconds:0}-second budget ran out";
            if (missing.Count == 0)
            {
                return new HealthProbeResult(ProbeName, "Files", HealthState.Healthy, $"No file missing ({scope}).", watch.Elapsed);
            }

            var data = missing.Take(NamedLimit).ToDictionary(name => name, _ => "missing on disk", StringComparer.Ordinal);
            // The names IN the description too: the Health page and its API show a probe's description, not its data - a page that
            // only said "1 file(s)" never said which (found in 10.68.0).
            var shown = missing.Take(DescribedLimit).ToList();
            var more = missing.Count > shown.Count ? $" and {missing.Count - shown.Count} more" : "";
            return new HealthProbeResult(
                ProbeName,
                "Files",
                HealthState.Degraded,
                $"{missing.Count} file(s) listed for this site are missing on disk and need uploading again: {string.Join(", ", shown)}{more} ({scope}).",
                watch.Elapsed,
                data);
        }

        private sealed class MissingFilesProbe(MissingFilesContributor owner) : IHealthProbe
        {
            public string Name => ProbeName;

            public string Dependency => "Files";

            public HealthProbeScope Scope => HealthProbeScope.Site;

            public Task<HealthProbeResult> CheckAsync(HealthProbeContext context, CancellationToken cancellationToken = default) =>
                owner.CheckAsync(context, cancellationToken);
        }
    }
}
