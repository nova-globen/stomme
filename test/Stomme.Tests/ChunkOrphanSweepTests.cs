using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-100 F1 / BL-114 (D109 item 14): chunk parts of an upload that never completes, and staging directories of
    /// an ingestion that died mid-flight, used to accumulate without bound. <see cref="ChunkOrphanSweeper"/> removes
    /// them once they are older than <c>Files:Chunks:OrphanMinutes</c>; it runs opportunistically on every chunk
    /// arrival and once a day per site from <see cref="PurgeJob"/>.
    /// </summary>
    [Collection(nameof(InstalledSiteRenderTests))]
    public class ChunkOrphanSweepTests : IDisposable
    {
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(60);

        private readonly ITestOutputHelper _output;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-chunk-sweep-" + Guid.NewGuid().ToString("N"));

        public ChunkOrphanSweepTests(ITestOutputHelper output)
        {
            _output = output;
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { /* best effort */ }
        }

        [Fact]
        public async Task Abandoned_parts_past_the_window_are_swept()
        {
            var now = DateTime.UtcNow;
            var chunks = Directory.CreateDirectory(Path.Combine(_root, FileIngestionPaths.ChunksDirectoryName, "abc")).FullName;
            var part1 = Touch(Path.Combine(chunks, "report.pdf.part_001_003"), now.AddHours(-3));
            var part2 = Touch(Path.Combine(chunks, "report.pdf.part_002_003"), now.AddHours(-2));
            var ingest = Directory.CreateDirectory(Path.Combine(_root, FileIngestionPaths.IngestDirectoryPrefix + "dead")).FullName;
            Touch(Path.Combine(ingest, "report.pdf"), now.AddHours(-2));
            Directory.SetLastWriteTimeUtc(ingest, now.AddHours(-2));
            // a legacy part the pre-guard controller wrote straight into a folder
            var legacy = Touch(Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "Folder")).FullName, "old.zip.part_001_002"), now.AddDays(-2));

            var swept = await ChunkOrphanSweeper.SweepOrphanedPartsAsync(_root, Window, now);
            _output.WriteLine($"[sweep] removed {swept}");

            Assert.False(File.Exists(part1));
            Assert.False(File.Exists(part2));
            Assert.False(Directory.Exists(ingest));
            Assert.False(File.Exists(legacy));
            Assert.Equal(4, swept);
        }

        [Fact]
        public async Task A_live_part_set_inside_the_window_is_not_swept()
        {
            var now = DateTime.UtcNow;
            var chunks = Directory.CreateDirectory(Path.Combine(_root, FileIngestionPaths.ChunksDirectoryName, "abc")).FullName;
            var live = Touch(Path.Combine(chunks, "movie.mp4.part_001_009"), now.AddMinutes(-5));
            var ingest = Directory.CreateDirectory(Path.Combine(_root, FileIngestionPaths.IngestDirectoryPrefix + "live")).FullName;
            Directory.SetLastWriteTimeUtc(ingest, now.AddMinutes(-1));
            // a user's file that merely LOOKS like a part name is not a part: the sweep matches part_NNN_TTT only
            var lookalike = Touch(Path.Combine(_root, "notes.part_one.txt"), now.AddDays(-30));
            var ordinary = Touch(Path.Combine(_root, "photo.jpg"), now.AddDays(-30));

            var swept = await ChunkOrphanSweeper.SweepOrphanedPartsAsync(_root, Window, now);

            Assert.Equal(0, swept);
            Assert.True(File.Exists(live));
            Assert.True(Directory.Exists(ingest));
            Assert.True(File.Exists(lookalike));
            Assert.True(File.Exists(ordinary));
        }

        [Fact]
        public async Task The_purge_job_sweep_runs_per_site()
        {
            using var scratch = await GateInstallTemplates.CloneAsync(new SqliteGateStore(), "localhost", _output);
            var app = await GateDrive.InstallAsync(scratch, _output);
            try
            {
                var (staleStaged, freshStaged, staleLegacy, log) = await GateDrive.InTenantAsync(app, "localhost", async (sp, alias) =>
                {
                    var environment = sp.GetRequiredService<IWebHostEnvironment>();
                    var now = DateTime.UtcNow;

                    var chunkDir = Directory.CreateDirectory(Path.Combine(
                        FileIngestionPaths.StagingRoot(environment.ContentRootPath, alias.TenantId, alias.SiteId), FileIngestionPaths.ChunksDirectoryName, "k")).FullName;
                    var stale = Touch(Path.Combine(chunkDir, "abandoned.txt.part_001_002"), now.AddDays(-1));
                    var fresh = Touch(Path.Combine(chunkDir, "arriving.txt.part_001_002"), now.AddMinutes(-2));

                    var sitePublic = Directory.CreateDirectory(Path.Combine(
                        environment.WebRootPath, "Content", "Tenants", alias.TenantId.ToString(), "Sites", alias.SiteId.ToString(), "Public")).FullName;
                    var legacy = Touch(Path.Combine(sitePublic, "legacy.zip.part_002_005"), now.AddDays(-3));

                    var job = new PurgeJob(sp.GetRequiredService<IServiceScopeFactory>());
                    var jobLog = await job.ExecuteJobAsync(sp);
                    return (stale, fresh, legacy, jobLog);
                });

                _output.WriteLine("[purge job log] " + log);
                Assert.False(File.Exists(staleStaged), "the purge job left an abandoned staged part");
                Assert.False(File.Exists(staleLegacy), "the purge job left an abandoned legacy part in the site's public content");
                Assert.True(File.Exists(freshStaged), "the purge job swept a part inside the window");
                Assert.Contains("Orphaned Upload Parts Purged", log);
            }
            finally
            {
                await GateDrive.ShutdownAsync(app);
            }
        }

        private static string Touch(string path, DateTime lastWriteUtc)
        {
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, lastWriteUtc);
            return path;
        }
    }
}
