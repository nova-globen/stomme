#nullable enable
using System;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-100's drive (D109 DoD, "the D30 bar on PostgreSQL AND RavenDB"): an installed site on each store, the Host signed
    /// in over HTTP; the SystemInfo "Clear all" endpoint empties the framework cache for real (a primed entry's factory runs
    /// again); a normal zip uploads through the Files API and extracts - files on disk AND rows; a zip bomb is refused with a
    /// 400 carrying its reason, nothing lands and no staging is left. The F1/O1 suites prove each rule on SQLite; this runs
    /// the same paths where the stores differ.
    /// </summary>
    /// <remarks>
    /// Not covered here, stated rather than implied: "Clear all" CLICKED in an interactive mode with zero console errors - no
    /// browser harness exists (the D109 correction, item 6); that half is one of the owner-sign-in click-throughs.
    /// </remarks>
    [Collection(nameof(InstalledSiteRenderTests))]
    public abstract class InstalledSiteBl100DriveTests<TFixture> : IClassFixture<TFixture>
        where TFixture : FileIngestionHostFixture
    {
        private const string ProbeKey = "bl100-drive-probe";

        private readonly TFixture _fixture;
        private readonly ITestOutputHelper _output;

        protected InstalledSiteBl100DriveTests(TFixture fixture, ITestOutputHelper output)
        {
            _fixture = fixture;
            _output = output;
        }

        /// <summary>The store's database class name - <c>PostgreSQLDatabase</c> or <c>RavenDatabase</c>.</summary>
        protected abstract string ExpectedDatabase { get; }

        [Fact]
        public async Task The_host_clears_the_cache_uploads_and_extracts_a_zip_and_a_bomb_is_refused()
        {
            // The point of this drive is the store: a fixture that fell back to SQLite would pass while proving nothing.
            var databaseType = _fixture.App.Services.GetRequiredService<IConfigManager>().GetDatabaseType();
            _output.WriteLine($"[store] {databaseType}");
            Assert.Contains(ExpectedDatabase, databaseType, StringComparison.Ordinal);

            var drive = await FileIngestionDrive.LoginAsHostAsync(_fixture, _output);

            // O1: Clear all, over HTTP as the Host.
            var cache = _fixture.App.Services.GetRequiredService<ICacheManager>();
            Assert.Equal("old", await cache.GetCacheAsync(ProbeKey, _ => Task.FromResult("old")));
            var clear = await GateDrive.SendJsonAsync(_fixture.App, drive.Client, drive.Cookies, FileIngestionHostFixture.Host, drive.UserId,
                HttpMethod.Post, "/api/System/cache/clear", null, _output, "clear all");
            Assert.True(clear.Status is HttpStatusCode.OK or HttpStatusCode.NoContent, $"clear all answered {(int)clear.Status}");
            var calls = 0;
            Assert.Equal("new", await cache.GetCacheAsync(ProbeKey, _ => { Interlocked.Increment(ref calls); return Task.FromResult("new"); }));
            Assert.Equal(1, calls);

            // F1: a normal zip, uploaded and extracted.
            var folder = await drive.NewFolderAsync();
            var zip = FileIngestionDrive.Zip(
                ("a.txt", FileIngestionDrive.Text("alpha"), CompressionLevel.Optimal),
                ("b.txt", FileIngestionDrive.Text("bravo"), CompressionLevel.Optimal));
            var upload = await drive.UploadAsync(folder, "normal.zip", zip);
            Assert.True(upload.Status is HttpStatusCode.NoContent or HttpStatusCode.OK, $"upload answered {(int)upload.Status}");
            var normal = (await drive.RowsAsync(folder)).Single(file => file.Name == "normal.zip");
            var unzip = await drive.UnzipAsync(normal.FileId);
            Assert.True(unzip.Status is HttpStatusCode.NoContent or HttpStatusCode.OK, $"unzip answered {(int)unzip.Status}");
            // A successful unzip removes the archive (the SQLite suite's expectation too).
            Assert.Equal(new[] { "a.txt", "b.txt" }, await drive.DiskNamesAsync(folder));
            Assert.Equal(new[] { "a.txt", "b.txt" }, await drive.RowNamesAsync(folder));

            // F1: a bomb (more entries than the cap) in a folder of its own - refused, with its reason, leaving nothing.
            var bombFolder = await drive.NewFolderAsync();
            var bomb = FileIngestionDrive.Zip(Enumerable.Range(0, FileIngestionHostFixture.MaxEntries + 1)
                .Select(i => ($"entry{i}.txt", FileIngestionDrive.Text("entry " + i), CompressionLevel.Optimal)).ToArray());
            await drive.UploadAsync(bombFolder, "bomb.zip", bomb);
            var bombRow = (await drive.RowsAsync(bombFolder)).Single(file => file.Name == "bomb.zip");
            var refused = await drive.UnzipAsync(bombRow.FileId);
            Assert.Equal(HttpStatusCode.BadRequest, refused.Status);
            Assert.Contains("EntryCount", refused.Body, StringComparison.Ordinal);
            Assert.Equal(new[] { "bomb.zip" }, await drive.DiskNamesAsync(bombFolder));
            Assert.Equal(new[] { "bomb.zip" }, await drive.RowNamesAsync(bombFolder));
            Assert.Empty(await drive.StagingLeftoversAsync());
        }
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class PostgresInstalledSiteBl100DriveTests(PostgresInstalledSiteBl100DriveTests.Fixture fixture, ITestOutputHelper output)
        : InstalledSiteBl100DriveTests<PostgresInstalledSiteBl100DriveTests.Fixture>(fixture, output)
    {
        protected override string ExpectedDatabase => "PostgreSQL";

        public sealed class Fixture : FileIngestionHostFixture
        {
            private protected override IGateStore NewStore() => new PostgresGateStore();
        }
    }

    [Trait("Category", "ExternalDatabase")]
    public sealed class RavenInstalledSiteBl100DriveTests(RavenInstalledSiteBl100DriveTests.Fixture fixture, ITestOutputHelper output)
        : InstalledSiteBl100DriveTests<RavenInstalledSiteBl100DriveTests.Fixture>(fixture, output)
    {
        protected override string ExpectedDatabase => "Raven";

        public sealed class Fixture : FileIngestionHostFixture
        {
            private protected override IGateStore NewStore() => new RavenGateStore();
        }
    }
}
