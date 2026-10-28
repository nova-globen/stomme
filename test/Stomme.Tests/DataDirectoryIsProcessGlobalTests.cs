#nullable enable
using System;
using System.IO;
using System.Linq;
using Stomme.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// <c>Constants.DataDirectory</c> is an AppDomain-GLOBAL mutable string, and this is what that costs a test
    /// run (BL-153).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The mechanism, measured here rather than inferred.</b> <c>Program.BuildAppAsync</c> does
    /// <c>AppDomain.CurrentDomain.SetData(Constants.DataDirectory, {ContentRootPath}/Data)</c>, and ten places in
    /// the framework resolve the <c>|DataDirectory|</c> token by reading it back —
    /// <c>ModuleAggregatePersistenceExtensions</c>, <c>ModuleRepositoryProviderRegistry</c>,
    /// <c>StommeTenantConnectionResolver</c>, <c>DatabaseManager</c> (three), FeatureManagement's
    /// <c>ServerStartup</c>, and the three <c>DBContext</c> bases. In production that is sound: one host, one
    /// process, one content root.
    /// </para>
    /// <para>
    /// <b>A test run breaks that assumption.</b> This assembly composes dozens of hosts in ONE process, and its
    /// classes run in parallel collections — so the value is whatever the most recently composed host wrote, for
    /// everybody. Four fixtures set it directly on top of that (<c>HostCompositionTests</c>,
    /// <c>AuthorizationBehaviorOrderingTests</c>, <c>FrameworkStoreProviderSelectionTests</c>,
    /// <c>LicensingHostComposition</c>).
    /// </para>
    /// <para>
    /// <b>That is the best explanation yet for BL-153</b>, where
    /// <c>SqliteNotificationAggregateConformanceTests</c> died on
    /// <c>DirectoryNotFoundException: …\stomme-host-composition\{guid}\Data</c> — a content root that suite
    /// never creates, owned by a class whose <c>Dispose</c> deletes it. A suite reading the global after that
    /// owner disposed gets a path that no longer exists. The row's first hypothesis (a fresh root missing its
    /// <c>Data</c> folder) was refuted by an isolation run; this is not refuted by one, because it needs two
    /// hosts.
    /// </para>
    /// <para>
    /// This suite PINS the mechanism; it does not fix it. The fix is to stop resolving the token from process
    /// state — the value is already derivable per host from <c>IWebHostEnvironment.ContentRootPath</c> — and that
    /// touches all ten read sites, so it is its own increment.
    /// </para>
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class DataDirectoryIsProcessGlobalTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly object? _previous;

        public DataDirectoryIsProcessGlobalTests(ITestOutputHelper output)
        {
            _output = output;
            _previous = AppDomain.CurrentDomain.GetData(Constants.DataDirectory);
        }

        [Fact]
        public void The_last_host_to_compose_decides_the_data_directory_for_every_other_one()
        {
            var first = Path.Combine(Path.GetTempPath(), "stomme-bl153-first", Guid.NewGuid().ToString("N"));
            var second = Path.Combine(Path.GetTempPath(), "stomme-bl153-second", Guid.NewGuid().ToString("N"));

            // Exactly what Program.BuildAppAsync does for each composed host, and what four fixtures do directly.
            AppDomain.CurrentDomain.SetData(Constants.DataDirectory, Path.Combine(first, "Data"));
            var afterFirst = AppDomain.CurrentDomain.GetData(Constants.DataDirectory)?.ToString();

            AppDomain.CurrentDomain.SetData(Constants.DataDirectory, Path.Combine(second, "Data"));
            var afterSecond = AppDomain.CurrentDomain.GetData(Constants.DataDirectory)?.ToString();

            _output.WriteLine($"[first ]  {afterFirst}");
            _output.WriteLine($"[second]  {afterSecond}");

            Assert.Equal(Path.Combine(first, "Data"), afterFirst);

            // The first host did not get its own directory back - there is only one slot, and the second host
            // took it. Every |DataDirectory| resolution in the process now answers with the SECOND host's root,
            // including one made on behalf of the first.
            Assert.Equal(Path.Combine(second, "Data"), afterSecond);
            Assert.NotEqual(afterFirst, afterSecond);
        }

        [Fact]
        public void A_connection_string_token_resolves_against_whoever_wrote_last_not_whoever_asks()
        {
            // The token expansion every |DataDirectory| reader performs, reduced to its essential line (see
            // ModuleAggregatePersistenceExtensions.ExpandDataDirectory and FeatureManagement's
            // ServerStartup.ResolveConnectionString, which are character-for-character this).
            var mine = Path.Combine(Path.GetTempPath(), "stomme-bl153-mine", Guid.NewGuid().ToString("N"));
            var theirs = Path.Combine(Path.GetTempPath(), "stomme-bl153-theirs", Guid.NewGuid().ToString("N"));

            AppDomain.CurrentDomain.SetData(Constants.DataDirectory, Path.Combine(mine, "Data"));

            // A sibling class composes its own host between my composition and my first resolution. Nothing about
            // my service provider changed; the process moved under it.
            AppDomain.CurrentDomain.SetData(Constants.DataDirectory, Path.Combine(theirs, "Data"));

            var resolved = Expand($"Data Source=|{Constants.DataDirectory}|Stomme.Features.db");
            _output.WriteLine($"[resolved] {resolved}");

            Assert.DoesNotContain("stomme-bl153-mine", resolved);
            Assert.Contains("stomme-bl153-theirs", resolved);

            // And when that sibling's Dispose deletes its content root - which every one of these fixtures does -
            // the path this process now hands out points at a directory that no longer exists. That is the shape
            // of the BL-153 failure, down to the DirectoryNotFoundException naming another suite's root.
            Assert.False(Directory.Exists(Path.GetDirectoryName(resolved.Replace("Data Source=", string.Empty))),
                "the sibling's root was never created here, standing in for one already disposed");
        }

        /// <summary>The framework's own token expansion, copied from the readers so it cannot drift silently.</summary>
        private static string Expand(string connectionString)
        {
            var dataDirectory = AppDomain.CurrentDomain.GetData(Constants.DataDirectory)?.ToString();
            return string.IsNullOrEmpty(dataDirectory)
                ? connectionString
                : connectionString.Replace($"|{Constants.DataDirectory}|", dataDirectory + Path.DirectorySeparatorChar);
        }

        public void Dispose() => AppDomain.CurrentDomain.SetData(Constants.DataDirectory, _previous);
    }
}
