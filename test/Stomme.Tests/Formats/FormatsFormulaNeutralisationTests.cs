#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Export;
using Stomme.Formats;
using Stomme.Modules.DataExport.Rendering;
using Stomme.Modules.Formats.Application;
using Stomme.Modules.Formats.Domain;
using Stomme.Tenancy.Domain;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Formats
{
    /// <summary>
    /// BL-94 increment 8 (D110, D108 refutation 11) - a Formats pattern whose literal output starts with a formula leader
    /// (<c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>) comes out neutralised in CSV and in XLSX, because a formatter's output is
    /// a string like any other cell. Both directions: every spreadsheet renderer the module ships neutralises a
    /// formatter's formula, and a PLANTED renderer that writes the formatter's text raw is caught by the same check.
    /// </summary>
    public sealed class FormatsFormulaNeutralisationTests(ITestOutputHelper output)
    {
        private static readonly DateTime Now = new(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);

        public static TheoryData<string, decimal, string> Leaders() => new()
        {
            { "'='0", 5m, "=5" },
            { "'+'0", 5m, "+5" },
            { "N0", -5m, "-5" },
            { "'@'0", 5m, "@5" },
        };

        [Theory]
        [MemberData(nameof(Leaders))]
        public async Task A_formats_pattern_whose_output_leads_with_a_formula_character_is_neutralised_in_CSV_and_XLSX(string pattern, decimal value, string rendered)
        {
            var formatter = await FormatterAsync(pattern);
            var column = new ExportColumn("Amount", ExportValueKind.Decimal, "Leader");
            var table = new StubTable("t", new[] { column }, new object?[][] { new object?[] { value } });

            Assert.Equal(rendered, formatter.Format(value, column));

            var csv = await RenderCsvAsync(table, formatter);
            output.WriteLine($"[csv] {pattern} -> {csv.Replace("\r\n", "\\r\\n")}");
            Assert.Equal("'" + rendered, csv.Split("\r\n")[1]);

            using var document = SpreadsheetDocument.Open(new MemoryStream(await RenderXlsxAsync(table, formatter)), false);
            var cell = Cells(document).Last();
            Assert.Equal(CellValues.InlineString, cell.DataType!.Value);
            Assert.Equal("'" + rendered, cell.InnerText);
            Assert.Equal(0, document.WorkbookPart!.WorksheetParts.Sum(part => part.Worksheet.Descendants<CellFormula>().Count()));
        }

        [Fact]
        public async Task Every_shipped_spreadsheet_renderer_neutralises_a_formatters_formula_and_a_planted_one_is_caught()
        {
            var shipped = typeof(CsvTabularRenderer).Assembly.GetTypes()
                .Where(type => !type.IsAbstract && typeof(ITabularRenderer).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null)
                .Select(type => (ITabularRenderer)Activator.CreateInstance(type)!)
                .Where(renderer => renderer.Format is ExportFormat.Csv or ExportFormat.Xlsx)
                .ToList();
            Assert.Equal(2, shipped.Count); // the vacuity floor: CSV and XLSX

            var violations = new List<string>();
            foreach (var renderer in shipped)
            {
                violations.AddRange(await ViolationsAsync(renderer));
            }

            Assert.Empty(violations);
            Assert.Contains(await ViolationsAsync(new PlantedRawCsvRenderer()), violation => violation.Contains(nameof(PlantedRawCsvRenderer), StringComparison.Ordinal));
        }

        /// <summary>Renders one cell through a formatter that answers <c>=EVIL()</c>; a spreadsheet renderer whose artifact carries it unquoted is a violation.</summary>
        private static async Task<IReadOnlyList<string>> ViolationsAsync(ITabularRenderer renderer)
        {
            var table = new StubTable("t", new[] { new ExportColumn("Value", ExportValueKind.Text) }, new object?[][] { new object?[] { "harmless" } });
            var violations = new List<string>();
            using var sink = new MemoryStream();
            await renderer.RenderAsync(new StubDocument(new[] { table }), sink, ExportRenderOptions.Default, new FormulaFormatter());
            var bytes = sink.ToArray();
            if (renderer.Format == ExportFormat.Csv)
            {
                var text = Encoding.UTF8.GetString(bytes);
                if (!text.Contains("'=EVIL()", StringComparison.Ordinal))
                {
                    violations.Add($"{renderer.GetType().Name}: a formatter's '=EVIL()' reached the CSV unquoted: {text.Replace("\r\n", "\\r\\n")}");
                }
            }
            else
            {
                using var document = SpreadsheetDocument.Open(new MemoryStream(bytes), false);
                var cells = Cells(document);
                if (cells.Any(cell => cell.InnerText == "=EVIL()") || document.WorkbookPart!.WorksheetParts.Any(part => part.Worksheet.Descendants<CellFormula>().Any()))
                {
                    violations.Add($"{renderer.GetType().Name}: a formatter's '=EVIL()' reached the workbook as a formula or unquoted");
                }
            }

            return violations;
        }

        private static async Task<FormatsExportCellFormatter> FormatterAsync(string pattern)
        {
            var definition = FormatDefinitionAggregate.Create(Guid.NewGuid(), TenantId.Default, 1, "Leader", FormatKind.Number, pattern, "en-US", null, Now);
            var formatter = new FormatsExportCellFormatter(new FormatsExportCellFormatterTests.InMemoryFormatDefinitions(definition), NullLogger<FormatsExportCellFormatter>.Instance);
            await formatter.PrepareAsync(1);
            return formatter;
        }

        private static async Task<string> RenderCsvAsync(ITabularTable table, IExportCellFormatter formatter)
        {
            using var sink = new MemoryStream();
            await new CsvTabularRenderer().RenderAsync(new StubDocument(new[] { table }), sink, ExportRenderOptions.Default, formatter);
            return Encoding.UTF8.GetString(sink.ToArray());
        }

        private static async Task<byte[]> RenderXlsxAsync(ITabularTable table, IExportCellFormatter formatter)
        {
            using var sink = new MemoryStream();
            await new XlsxTabularRenderer().RenderAsync(new StubDocument(new[] { table }), sink, ExportRenderOptions.Default, formatter);
            return sink.ToArray();
        }

        private static List<Cell> Cells(SpreadsheetDocument document)
        {
            var sheet = document.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().First();
            var part = (WorksheetPart)document.WorkbookPart.GetPartById(sheet.Id!.Value!);
            return part.Worksheet.Descendants<Cell>().ToList();
        }

        private sealed class FormulaFormatter : IExportCellFormatter
        {
            public string? Format(object? value, ExportColumn column) => "=EVIL()";
        }

        /// <summary>the predecessor platform's shape: the formatter's text written as it came.</summary>
        private sealed class PlantedRawCsvRenderer : ITabularRenderer
        {
            public ExportFormat Format => ExportFormat.Csv;

            public async Task<RenderOutcome> RenderAsync(ITabularDocument document, Stream destination, ExportRenderOptions options, IExportCellFormatter formatter, CancellationToken ct = default)
            {
                var rows = 0L;
                await foreach (var table in document.ReadTablesAsync(ct))
                {
                    await foreach (var row in table.ReadRowsAsync(ct))
                    {
                        var line = string.Join(",", row.Select((value, i) => formatter.Format(value, table.Columns[i]) ?? Convert.ToString(value) ?? string.Empty)) + "\r\n";
                        await destination.WriteAsync(Encoding.UTF8.GetBytes(line), ct);
                        rows++;
                    }
                }

                return new RenderOutcome(rows);
            }
        }

        private sealed class StubDocument(IReadOnlyList<ITabularTable> tables) : ITabularDocument
        {
            public async IAsyncEnumerable<ITabularTable> ReadTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                foreach (var table in tables)
                {
                    yield return table;
                    await Task.Yield();
                }
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class StubTable(string name, ExportColumn[] columns, object?[][] rows) : ITabularTable
        {
            public string Name => name;

            public IReadOnlyList<ExportColumn> Columns => columns;

            public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                foreach (var row in rows)
                {
                    yield return row;
                    await Task.Yield();
                }
            }
        }
    }
}
