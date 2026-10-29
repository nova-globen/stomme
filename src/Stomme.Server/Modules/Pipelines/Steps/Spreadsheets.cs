#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Export;
using Stomme.Modules.DataExport.Rendering;
using Stomme.Modules.DataExport.Sources;
using Stomme.Modules.Datasets.Application;
using Microsoft.Extensions.Options;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// The first sheet of a spreadsheet in the site's pipeline folder as RECORDS (BL-95's residue "XLSX file shapes"):
    /// read by the parser the Datasets upload path already trusts - the package checked before it is opened (part count,
    /// declared sizes, the compression ratio), then read by SAX, the first row the column names, dates as dates - so a
    /// spreadsheet bomb is refused here exactly as it is at an upload. A <c>.csv</c> is read the same way. The records are the
    /// step's result, the JSON array the other data steps chain on; at most <see cref="MaxRows"/> of them.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Files", "Read the first sheet of a spreadsheet (XLSX, or CSV) from the site's pipeline folder into records.", DisplayName = "Read spreadsheet")]
    public class ReadSpreadsheet : DataStep
    {
        public const int DefaultMaxRows = 10_000;
        public const int MaxMaxRows = 100_000;

        [Input(UIHint = Stomme.Models.Pipelines.PipelineInputHints.PipelineFile, Description = "The file's path, relative to the site's pipeline folder (.xlsx or .csv).", Order = 1)]
        public Input<string> Path { get; set; } = null!;

        [Input(DisplayName = "Maximum rows", Description = "At most this many rows become records (1 to 100,000); the step says when the sheet held more.", Order = 2)]
        public Input<int> MaxRows { get; set; } = new(new Literal<int>(DefaultMaxRows));

        [Output(DisplayName = "Row count", Description = "The rows in the sheet, below its header - read or not.")]
        public Output<long>? RowCount { get; set; }

        [Output(Description = "True when the sheet held more rows than were read.")]
        public Output<bool>? Truncated { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var relative = Required(context, Path, "Path");
            var extension = System.IO.Path.GetExtension(relative);
            if (!string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase) && !string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
            {
                await FailAsync(context, "FORMAT_UNSUPPORTED", $"'{relative}' is not a .xlsx or .csv file.").ConfigureAwait(false);
                return;
            }

            var full = PipelineFileSandbox.Resolve(context, relative, out var refusal);
            if (full is null)
            {
                await FailAsync(context, "PATH_REFUSED", refusal!).ConfigureAwait(false);
                return;
            }

            if (!File.Exists(full))
            {
                await FailAsync(context, "FILE_NOT_FOUND", $"No file at '{relative}'.").ConfigureAwait(false);
                return;
            }

            var max = context.GetService<IOptions<PipelineStepOptions>>()?.Value.Files.MaxFileBytes ?? new FileStepOptions().MaxFileBytes;
            if (new FileInfo(full).Length > max)
            {
                await FailAsync(context, "FILE_TOO_LARGE", $"'{relative}' is larger than Pipelines:Files:MaxFileBytes ({max}).").ConfigureAwait(false);
                return;
            }

            var cap = Math.Clamp(MaxRows.GetOrDefault(context), 1, MaxMaxRows);
            UploadedSample sheet;
            try
            {
                await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                sheet = UploadedDatasetParser.Parse(stream, relative, cap, (int)Math.Min(max, int.MaxValue));
            }
            catch (DatasetUploadRefusedException ex)
            {
                await FailAsync(context, "SPREADSHEET_REFUSED", ex.Message).ConfigureAwait(false); // the parser's own words, never the file's
                return;
            }

            var truncated = sheet.RowCount > cap;
            context.Set(RowCount, sheet.RowCount);
            context.Set(Truncated, truncated);
            if (truncated)
            {
                context.AddExecutionLogEntry("Truncated", $"Read {cap} of the sheet's {sheet.RowCount} rows.");
            }

            await SucceedAsync(context, sheet.SampleJson).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// RECORDS as an XLSX spreadsheet in the site's pipeline folder (the other half of the residue): one sheet, the columns
    /// the records' keys in first-seen order, each value keeping its JSON type - written by the export renderer, so a cell that
    /// would start a formula is neutralised as it is in every export, and the file is streamed, never held whole. It is written
    /// beside its destination and then moved over it, so a failed write leaves the previous file as it was.
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "Files", "Write records to an XLSX spreadsheet in the site's pipeline folder.", DisplayName = "Write spreadsheet")]
    public class WriteSpreadsheet : DataStep
    {
        [Input(UIHint = Stomme.Models.Pipelines.PipelineInputHints.PipelineFile, Description = "The file's path, relative to the site's pipeline folder; it must end in .xlsx. Folders are created.", Order = 1)]
        public Input<string> Path { get; set; } = null!;

        [Input(UIHint = InputUIHints.MultiLine, Description = "The records, a JSON array of objects. Empty: the previous step's result.", Order = 2)]
        public Input<string?>? Records { get; set; }

        [Input(DisplayName = "Sheet name", Description = "The sheet's name. Empty: Sheet1.", Order = 3)]
        public Input<string?>? SheetName { get; set; }

        [Output(DisplayName = "Row count")]
        public Output<long>? RowCount { get; set; }

        [Output(DisplayName = "Full path", Description = "Where the file was written.")]
        public Output<string>? FullPath { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var relative = Required(context, Path, "Path");
            if (!string.Equals(System.IO.Path.GetExtension(relative), ".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                await FailAsync(context, "FORMAT_UNSUPPORTED", $"'{relative}' does not end in .xlsx.").ConfigureAwait(false);
                return;
            }

            var full = PipelineFileSandbox.Resolve(context, relative, out var refusal);
            if (full is null)
            {
                await FailAsync(context, "PATH_REFUSED", refusal!).ConfigureAwait(false);
                return;
            }

            var text = TextOrLastResult(context, Records) ?? "";
            List<string> columns;
            try
            {
                columns = ColumnsOf(text);
            }
            catch (FormatException ex)
            {
                await FailAsync(context, "RECORDS_INVALID", ex.Message).ConfigureAwait(false);
                return;
            }

            if (context.GetService<ExportRendererRegistry>() is not { } renderers || !renderers.TryGet(ExportFormat.Xlsx, out var renderer)
                || context.GetService<IExportCellFormatter>() is not { } formatter)
            {
                await FailAsync(context, "XLSX_UNAVAILABLE", "No XLSX renderer is composed in this host.").ConfigureAwait(false);
                return;
            }

            var sheetName = SheetName?.GetOrDefault(context);
            var table = new JsonRowsTable(string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName.Trim(), columns.Select(name => new ExportColumn(name, ExportValueKind.Text)).ToList(), text);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            var partial = full + "." + Guid.NewGuid().ToString("N") + ".partial";
            long rows;
            try
            {
                await using (var destination = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await using var document = new ExportTablesDocument(new ITabularTable[] { table });
                    rows = (await renderer.RenderAsync(document, destination, new ExportRenderOptions(), formatter, context.CancellationToken).ConfigureAwait(false)).RowCount;
                }

                var max = context.GetService<IOptions<PipelineStepOptions>>()?.Value.Files.MaxFileBytes ?? new FileStepOptions().MaxFileBytes;
                if (new FileInfo(partial).Length > max)
                {
                    File.Delete(partial);
                    await FailAsync(context, "FILE_TOO_LARGE", $"Writing '{relative}' would exceed Pipelines:Files:MaxFileBytes ({max}).").ConfigureAwait(false);
                    return;
                }

                File.Move(partial, full, overwrite: true);
            }
            catch (Exception) when (File.Exists(partial))
            {
                File.Delete(partial);
                throw;
            }

            context.Set(RowCount, rows);
            context.Set(FullPath, full);
            await SucceedAsync(context, relative).ConfigureAwait(false);
        }

        // The records' keys in first-seen order; anything but an array of objects is refused with the reason.
        internal static List<string> ColumnsOf(string recordsJson)
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(string.IsNullOrWhiteSpace(recordsJson) ? "[]" : recordsJson);
            }
            catch (JsonException ex)
            {
                throw new FormatException("The records are not JSON: " + ex.Message);
            }

            if (root is not JsonArray array)
            {
                throw new FormatException("The records are not a JSON array.");
            }

            var columns = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in array)
            {
                if (item is not JsonObject record)
                {
                    throw new FormatException("Every record must be a JSON object.");
                }

                foreach (var (name, _) in record)
                {
                    if (seen.Add(name))
                    {
                        columns.Add(name);
                    }
                }
            }

            return columns;
        }
    }
}
