#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-95's residue "ZIP file shapes" (2026-10-03): Zip files and Unzip files, run by the real engine on the site's pipeline
    /// folder - a round trip with subfolders, a pattern, an archive written into the folder it zips, and the refusals an archive
    /// that is not trusted earns: an entry that climbs out (zip slip), more entries than <c>Files:Unzip:MaxEntries</c>, more
    /// decompressed bytes than <c>Files:Unzip:MaxTotalBytes</c>, an entry that expands past the ratio, a file already in the
    /// destination, a file that is not a zip. The caps are read from the host's configuration root, set small here.
    /// </summary>
    public sealed class ZipArchiveStepsTests : IAsyncLifetime
    {
        private const int MaxEntries = 4;
        private const long MaxTotalBytes = 300_000;

        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-zips-" + Guid.NewGuid().ToString("N"));
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ResultSink>();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions { Files = { Root = _root } }));
            services.AddSingleton<IConfigurationRoot>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Files:Unzip:MaxEntries"] = MaxEntries.ToString(),
                ["Files:Unzip:MaxTotalBytes"] = MaxTotalBytes.ToString(),
            }).Build());
            services.AddEngine(module =>
            {
                module.AddStepsFrom<PipelineStepsMarker>();
                module.AddStep<CaptureLastResult>();
            });
            _services = services.BuildServiceProvider();
            using var scope = _services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IRegistriesPopulator>().PopulateAsync();
        }

        public async Task DisposeAsync()
        {
            await _services.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        public sealed class ResultSink
        {
            public string? Value { get; set; }
        }

        [Step("Stomme.Tests", "Tests", "Captures the last result (zips).")]
        public sealed class CaptureLastResult : CodeStep
        {
            protected override void Execute(StepExecutionContext context) =>
                context.GetRequiredService<ResultSink>().Value = context.GetLastResult()?.ToString();
        }

        private async Task<RunPipelineResult> RunAsync(IStep step)
        {
            using var scope = _services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPipelineRunner>().RunAsync(step);
        }

        private string Captured => _services.GetRequiredService<ResultSink>().Value ?? "";

        private static IDictionary<string, object> OutputsOf<T>(RunPipelineResult run) where T : IStep =>
            run.Journal.StepExecutionContexts.Single(c => c.Step is T).GetOutputs();

        private static string ErrorOf<T>(RunPipelineResult run) where T : IStep => (string)OutputsOf<T>(run)["Error"]!;

        private string SiteFile(string relative) => Path.Combine(_root, "default", relative);

        private void Put(string relative, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SiteFile(relative))!);
            File.WriteAllText(SiteFile(relative), text);
        }

        private async Task ArchiveAsync(string relative, params (string Name, byte[] Content)[] entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SiteFile(relative))!);
            using var zip = ZipFile.Open(SiteFile(relative), ZipArchiveMode.Create);
            foreach (var (name, content) in entries)
            {
                await using var entry = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                await entry.WriteAsync(content);
            }
        }

        private static byte[] Noise(int length)
        {
            var bytes = new byte[length];
            new Random(length).NextBytes(bytes); // incompressible, so the ratio cap stays out of the way
            return bytes;
        }

        [Fact]
        public async Task A_folder_round_trips_through_an_archive_with_its_subfolders_and_the_files_come_back_as_records()
        {
            Put("in/a.csv", "sku,qty\nA1,3\n");
            Put("in/sub/b.txt", "hello");

            var zipped = await RunAsync(new CreateZipArchive { Source = new("in"), Destination = new("out/in.zip") });
            Assert.Equal(PipelineSubStatus.Finished, zipped.PipelineState.SubStatus);
            Assert.Equal(2, (int)OutputsOf<CreateZipArchive>(zipped)["EntryCount"]);
            Assert.Empty(Directory.GetFiles(SiteFile("out"), "*.partial"));
            using (var archive = ZipFile.OpenRead(SiteFile("out/in.zip")))
            {
                Assert.Equal(new[] { "a.csv", "sub/b.txt" }, archive.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
            }

            var unzipped = await RunAsync(new Sequence { Steps = { new ExtractZipArchive { Path = new("out/in.zip"), Destination = new("restored") }, new CaptureLastResult() } });
            Assert.Equal(PipelineSubStatus.Finished, unzipped.PipelineState.SubStatus);
            Assert.Equal(2, (int)OutputsOf<ExtractZipArchive>(unzipped)["FileCount"]);
            Assert.Equal("sku,qty\nA1,3\n", File.ReadAllText(SiteFile("restored/a.csv")));
            Assert.Equal("hello", File.ReadAllText(SiteFile("restored/sub/b.txt")));
            Assert.Empty(Directory.GetDirectories(SiteFile(""), "*.partial"));

            var records = JsonNode.Parse(Captured)!.AsArray().Select(record => (record!["path"]!.GetValue<string>(), record["bytes"]!.GetValue<long>())).Order().ToList();
            Assert.Equal(new[] { ("restored/a.csv", 13L), ("restored/sub/b.txt", 5L) }, records);
        }

        [Fact]
        public async Task A_pattern_takes_only_its_files_and_an_archive_written_into_the_folder_it_zips_never_contains_itself()
        {
            Put("data/one.csv", "1");
            Put("data/two.csv", "2");
            Put("data/notes.txt", "n");

            var csv = await RunAsync(new CreateZipArchive { Source = new("data"), Destination = new("csv.zip"), Pattern = new("*.csv") });
            Assert.Equal(2, (int)OutputsOf<CreateZipArchive>(csv)["EntryCount"]);

            await RunAsync(new CreateZipArchive { Source = new("data"), Destination = new("data/all.zip") });
            var again = await RunAsync(new CreateZipArchive { Source = new("data"), Destination = new("data/all.zip") });
            Assert.Equal(3, (int)OutputsOf<CreateZipArchive>(again)["EntryCount"]);
            using var archive = ZipFile.OpenRead(SiteFile("data/all.zip"));
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith(".zip", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("../evil.txt")]
        [InlineData("deep/../../evil.txt")]
        [InlineData("/evil.txt")]
        [InlineData("c:/evil.txt")]
        public async Task An_entry_that_is_not_a_plain_relative_path_refuses_the_archive_and_nothing_is_extracted(string hostile)
        {
            await ArchiveAsync("slip.zip", ("fine.txt", new byte[] { 1 }), (hostile, new byte[] { 2 }));

            var run = await RunAsync(new ExtractZipArchive { Path = new("slip.zip"), Destination = new("x/y"), OnFailure = new(FailurePolicy.Continue) });

            Assert.StartsWith("ENTRY_REFUSED:", ErrorOf<ExtractZipArchive>(run));
            Assert.False(File.Exists(Path.Combine(_root, "evil.txt")));
            Assert.False(File.Exists(SiteFile("evil.txt")));
            Assert.False(File.Exists(SiteFile("x/evil.txt")));
            Assert.False(Directory.Exists(SiteFile("x/y")));
            Assert.Empty(Directory.GetDirectories(SiteFile("x"), "*.partial"));
        }

        [Fact]
        public async Task More_entries_than_the_cap_are_refused_both_ways()
        {
            await ArchiveAsync("many.zip", Enumerable.Range(1, MaxEntries + 1).Select(i => ($"f{i}.txt", new byte[] { (byte)i })).ToArray());
            var unzip = await RunAsync(new ExtractZipArchive { Path = new("many.zip"), Destination = new("many"), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("TOO_MANY_FILES:", ErrorOf<ExtractZipArchive>(unzip));
            Assert.False(Directory.Exists(SiteFile("many")));

            foreach (var i in Enumerable.Range(1, MaxEntries + 1))
            {
                Put($"lots/f{i}.txt", "x");
            }

            var zip = await RunAsync(new CreateZipArchive { Source = new("lots"), Destination = new("lots.zip"), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("TOO_MANY_FILES:", ErrorOf<CreateZipArchive>(zip));
            Assert.False(File.Exists(SiteFile("lots.zip")));
        }

        [Fact]
        public async Task Decompressed_bytes_past_the_total_cap_are_refused_as_they_are_counted()
        {
            await ArchiveAsync("big.zip", ("one.bin", Noise(200_000)), ("two.bin", Noise(200_001)));

            var run = await RunAsync(new ExtractZipArchive { Path = new("big.zip"), Destination = new("big"), OnFailure = new(FailurePolicy.Continue) });

            var error = ErrorOf<ExtractZipArchive>(run);
            Assert.StartsWith("FILE_TOO_LARGE:", error);
            Assert.Contains("Files:Unzip:MaxTotalBytes", error, StringComparison.Ordinal);
            Assert.False(Directory.Exists(SiteFile("big")));
        }

        [Fact]
        public async Task An_entry_that_expands_past_the_ratio_is_refused()
        {
            // One megabyte of zeros deflates to about a kilobyte: a ratio far past 100, over the 64 KiB floor.
            await ArchiveAsync("bomb.zip", ("zeros.bin", new byte[1024 * 1024]));

            var run = await RunAsync(new ExtractZipArchive { Path = new("bomb.zip"), Destination = new("bomb"), OnFailure = new(FailurePolicy.Continue) });

            var error = ErrorOf<ExtractZipArchive>(run);
            Assert.StartsWith("ARCHIVE_REFUSED:", error);
            Assert.Contains("expands more than 100 times", error, StringComparison.Ordinal);
            Assert.False(Directory.Exists(SiteFile("bomb")));
        }

        [Fact]
        public async Task A_file_already_in_the_destination_stops_the_extraction_unless_overwrite_is_set()
        {
            await ArchiveAsync("v1.zip", ("a.txt", "new"u8.ToArray()), ("b.txt", "also new"u8.ToArray()));
            Put("dest/b.txt", "old");

            var refused = await RunAsync(new ExtractZipArchive { Path = new("v1.zip"), Destination = new("dest"), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("FILE_EXISTS:", ErrorOf<ExtractZipArchive>(refused));
            Assert.Equal("old", File.ReadAllText(SiteFile("dest/b.txt")));
            Assert.False(File.Exists(SiteFile("dest/a.txt"))); // checked for every file before the first move

            var replaced = await RunAsync(new ExtractZipArchive { Path = new("v1.zip"), Destination = new("dest"), Overwrite = new(true) });
            Assert.Equal(PipelineSubStatus.Finished, replaced.PipelineState.SubStatus);
            Assert.Equal("also new", File.ReadAllText(SiteFile("dest/b.txt")));
            Assert.Equal("new", File.ReadAllText(SiteFile("dest/a.txt")));
        }

        [Fact]
        public async Task Another_extension_a_path_out_of_the_folder_a_missing_source_and_a_file_that_is_not_a_zip_are_refused()
        {
            Put("not-a.zip", "plain text");

            Assert.StartsWith("FORMAT_UNSUPPORTED:", ErrorOf<CreateZipArchive>(await RunAsync(new CreateZipArchive { Source = new("in"), Destination = new("out.tar"), OnFailure = new(FailurePolicy.Continue) })));
            Assert.StartsWith("FORMAT_UNSUPPORTED:", ErrorOf<ExtractZipArchive>(await RunAsync(new ExtractZipArchive { Path = new("in.rar"), Destination = new("x"), OnFailure = new(FailurePolicy.Continue) })));
            Assert.StartsWith("PATH_REFUSED:", ErrorOf<CreateZipArchive>(await RunAsync(new CreateZipArchive { Source = new("../"), Destination = new("out.zip"), OnFailure = new(FailurePolicy.Continue) })));
            Assert.StartsWith("PATH_REFUSED:", ErrorOf<ExtractZipArchive>(await RunAsync(new ExtractZipArchive { Path = new("not-a.zip"), Destination = new("../out"), OnFailure = new(FailurePolicy.Continue) })));
            Assert.StartsWith("FILE_NOT_FOUND:", ErrorOf<CreateZipArchive>(await RunAsync(new CreateZipArchive { Source = new("nothing-here"), Destination = new("out.zip"), OnFailure = new(FailurePolicy.Continue) })));
            Assert.StartsWith("ARCHIVE_CORRUPT:", ErrorOf<ExtractZipArchive>(await RunAsync(new ExtractZipArchive { Path = new("not-a.zip"), Destination = new("x"), OnFailure = new(FailurePolicy.Continue) })));
        }
    }
}
