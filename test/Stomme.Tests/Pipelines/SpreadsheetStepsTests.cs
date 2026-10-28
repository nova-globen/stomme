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
using Stomme.Export;
using Stomme.Modules.DataExport.Rendering;
using Stomme.Modules.DataExport.Startup;
using Stomme.Modules.Pipelines.Steps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests.Pipelines
{
    /// <summary>
    /// BL-95's residue "XLSX file shapes" (2026-09-28): Write spreadsheet over the export renderer and Read spreadsheet over the
    /// Datasets upload parser, run by the real engine on the site's pipeline folder - a round trip keeping each value's type,
    /// the columns in first-seen order, a formula-leading value neutralised, a cap that says it truncated, and the refusals:
    /// another extension, a path out of the folder, records that are not an array of objects, and a compression bomb.
    /// </summary>
    public sealed class SpreadsheetStepsTests : IAsyncLifetime
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "stomme-spreadsheets-" + Guid.NewGuid().ToString("N"));
        private ServiceProvider _services = default!;

        public async Task InitializeAsync()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ResultSink>();
            services.AddSingleton<IOptions<PipelineStepOptions>>(Options.Create(new PipelineStepOptions { Files = { Root = _root } }));
            services.AddSingleton<ITabularRenderer, XlsxTabularRenderer>();
            services.AddSingleton<ExportRendererRegistry>();
            services.AddSingleton<IExportCellFormatter, InvariantExportCellFormatter>();
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

        [Step("Stomme.Tests", "Tests", "Captures the last result (spreadsheets).")]
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

        private string SiteFile(string relative) => Path.Combine(_root, "default", relative);

        [Fact]
        public async Task Records_round_trip_through_a_spreadsheet_keeping_types_order_and_neutralising_a_formula()
        {
            const string records = """[{"sku":"A1","qty":3,"active":true},{"sku":"B2","qty":7.5,"note":"=HYPERLINK(\"http://x\")"}]""";

            var written = await RunAsync(new WriteSpreadsheet { Path = new("out/stock.xlsx"), Records = new(records), SheetName = new("Stock") });
            Assert.Equal(PipelineSubStatus.Finished, written.PipelineState.SubStatus);
            Assert.Equal(2L, (long)OutputsOf<WriteSpreadsheet>(written)["RowCount"]);
            Assert.True(File.Exists(SiteFile("out/stock.xlsx")));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(SiteFile("out/stock.xlsx"))!, "*.partial"));

            var read = await RunAsync(new Sequence { Steps = { new ReadSpreadsheet { Path = new("out/stock.xlsx") }, new CaptureLastResult() } });
            Assert.Equal(PipelineSubStatus.Finished, read.PipelineState.SubStatus);
            var rows = JsonNode.Parse(Captured)!.AsArray();
            Assert.Equal(2, rows.Count);
            Assert.Equal(new[] { "sku", "qty", "active" }, rows[0]!.AsObject().Select(p => p.Key).Take(3));
            Assert.Equal("A1", rows[0]!["sku"]!.ToString());
            Assert.Equal("3", rows[0]!["qty"]!.ToString());
            Assert.Equal("7.5", rows[1]!["qty"]!.ToString());
            Assert.StartsWith("'=HYPERLINK", rows[1]!["note"]!.ToString(), StringComparison.Ordinal); // neutralised, never a live formula
            Assert.Equal((2L, false), ((long)OutputsOf<ReadSpreadsheet>(read)["RowCount"], (bool)OutputsOf<ReadSpreadsheet>(read)["Truncated"]));
        }

        [Fact]
        public async Task A_cap_reads_that_many_and_says_the_sheet_held_more()
        {
            var many = "[" + string.Join(",", Enumerable.Range(1, 5).Select(i => "{\"n\":" + i + "}")) + "]";
            await RunAsync(new WriteSpreadsheet { Path = new("five.xlsx"), Records = new(many) });

            var read = await RunAsync(new Sequence { Steps = { new ReadSpreadsheet { Path = new("five.xlsx"), MaxRows = new(2) }, new CaptureLastResult() } });

            Assert.Equal(2, JsonNode.Parse(Captured)!.AsArray().Count);
            Assert.Equal((5L, true), ((long)OutputsOf<ReadSpreadsheet>(read)["RowCount"], (bool)OutputsOf<ReadSpreadsheet>(read)["Truncated"]));
            Assert.Contains(read.Journal.PipelineExecutionLogEntries, entry => entry.EventName == "Truncated");
        }

        [Fact]
        public async Task Another_extension_a_path_out_of_the_folder_and_records_that_are_not_objects_are_refused()
        {
            var txt = await RunAsync(new ReadSpreadsheet { Path = new("notes.txt"), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("FORMAT_UNSUPPORTED:", (string)OutputsOf<ReadSpreadsheet>(txt)["Error"]!);

            var escape = await RunAsync(new WriteSpreadsheet { Path = new("../outside.xlsx"), Records = new("[]"), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("PATH_REFUSED:", (string)OutputsOf<WriteSpreadsheet>(escape)["Error"]!);

            var notObjects = await RunAsync(new WriteSpreadsheet { Path = new("bad.xlsx"), Records = new("[1,2]"), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("RECORDS_INVALID:", (string)OutputsOf<WriteSpreadsheet>(notObjects)["Error"]!);

            var csvPath = await RunAsync(new WriteSpreadsheet { Path = new("out.csv"), Records = new("[]"), OnFailure = new(FailurePolicy.Continue) });
            Assert.StartsWith("FORMAT_UNSUPPORTED:", (string)OutputsOf<WriteSpreadsheet>(csvPath)["Error"]!);
        }

        [Fact]
        public async Task A_compression_bomb_named_xlsx_is_refused_before_it_is_opened()
        {
            // One megabyte of zeros deflates to about a kilobyte: a ratio far past the parser's 100, over its 64 KiB floor.
            Directory.CreateDirectory(Path.Combine(_root, "default"));
            using (var zip = ZipFile.Open(SiteFile("bomb.xlsx"), ZipArchiveMode.Create))
            {
                await using var entry = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.SmallestSize).Open();
                await entry.WriteAsync(new byte[1024 * 1024]);
            }

            var run = await RunAsync(new ReadSpreadsheet { Path = new("bomb.xlsx"), OnFailure = new(FailurePolicy.Continue) });

            var error = (string)OutputsOf<ReadSpreadsheet>(run)["Error"]!;
            Assert.StartsWith("SPREADSHEET_REFUSED:", error);
            Assert.Contains("expands more than 100:1", error, StringComparison.Ordinal); // the ratio guard, before the package is opened
        }
    }
}
