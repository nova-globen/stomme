#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Export;
using Stomme.Modules.DataExport.Rendering;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The XLSX renderer: what it writes, that the result OPENS, and that no cell can execute (BL-85, D108).
    /// </summary>
    /// <remarks>
    /// The renderer bypasses the SDK's document model to keep its package write-only (see
    /// <c>XlsxStreamingMeasurementTests</c>), so nothing on the write side checks that the parts, content types
    /// and relationships add up to a workbook. Every case here therefore reads the artifact back through
    /// <see cref="SpreadsheetDocument.Open(Stream, bool)"/> - the SDK's READ side is the independent witness.
    /// </remarks>
    public class XlsxRendererTests
    {
        private readonly ITestOutputHelper _output;

        public XlsxRendererTests(ITestOutputHelper output) => _output = output;

        // ---- the artifact is a real workbook -------------------------------------------------------------

        [Fact]
        public async Task The_artifact_opens_and_passes_the_SDK_schema_validator()
        {
            var bytes = await RenderAsync(new[]
            {
                Table("Orders",
                    new[]
                    {
                        new ExportColumn("Id", ExportValueKind.Integer),
                        new ExportColumn("Customer", ExportValueKind.Text),
                        new ExportColumn("Total", ExportValueKind.Decimal),
                        new ExportColumn("Paid", ExportValueKind.Boolean),
                        new ExportColumn("When", ExportValueKind.DateTimeUtc),
                        new ExportColumn("Ref", ExportValueKind.Guid),
                    },
                    new object?[][]
                    {
                        new object?[] { 1, "Ada", 12.50m, true, new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc), Guid.Empty },
                        new object?[] { 2, null, -3.25m, false, null, null },
                    }),
                Table("Second", new[] { new ExportColumn("A", ExportValueKind.Text) }, new object?[][] { new object?[] { "x" } }),
            });

            using var document = Open(bytes);
            var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document).ToList();
            foreach (var error in errors)
            {
                _output.WriteLine($"[validator] {error.Path?.XPath}: {error.Description}");
            }

            Assert.Empty(errors);
            Assert.Equal(new[] { "Orders", "Second" }, SheetNames(document));
        }

        [Fact]
        public async Task The_validator_DOES_report_a_malformed_sheet()
        {
            // The other direction: Assert.Empty(errors) above is only worth something if this validator is
            // capable of objecting to what a renderer like this one could get wrong.
            using var stream = new MemoryStream();
            using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
            {
                var workbookPart = document.AddWorkbookPart();
                var sheetPart = workbookPart.AddNewPart<WorksheetPart>();
                sheetPart.Worksheet = new Worksheet(new SheetData(new Row(
                    new Cell { CellReference = "not-a-reference", DataType = CellValues.Number, CellValue = new CellValue("1") })));
                workbookPart.Workbook = new Workbook(new Sheets(
                    new Sheet { Name = "bad[name]", SheetId = 1, Id = workbookPart.GetIdOfPart(sheetPart) }));
            }

            using var reopened = Open(stream.ToArray());
            var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(reopened).ToList();
            _output.WriteLine($"[planted] {errors.Count} validator error(s)");
            Assert.NotEmpty(errors);
        }

        [Fact]
        public async Task It_renders_into_a_sink_that_can_neither_seek_nor_be_read()
        {
            // The destination in production is a counting stream over the artifact: forward-only. A renderer
            // that needed to seek back (as the read-write package does) would fail here rather than on a server.
            var sink = new ForwardOnlySink();
            var outcome = await new XlsxTabularRenderer().RenderAsync(
                new StubDocument(new[] { Table("t", new[] { new ExportColumn("A", ExportValueKind.Integer) }, new object?[][] { new object?[] { 1 } }) }),
                sink, ExportRenderOptions.Default, new PassThroughFormatter());

            Assert.Equal(1, outcome.RowCount);
            using var document = Open(sink.ToArray());
            Assert.Single(SheetNames(document));
        }

        [Fact]
        public async Task An_empty_document_is_still_a_workbook_that_opens()
        {
            var bytes = await RenderAsync(Array.Empty<ITabularTable>());

            using var document = Open(bytes);
            Assert.Equal(new[] { "Sheet1" }, SheetNames(document));
            Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document));
        }

        // ---- cell typing -----------------------------------------------------------------------------------

        [Fact]
        public async Task A_value_that_honours_its_column_is_typed_and_one_that_does_not_is_TEXT()
        {
            var bytes = await RenderAsync(new[]
            {
                Table("t",
                    new[]
                    {
                        new ExportColumn("Count", ExportValueKind.Integer),
                        new ExportColumn("Ratio", ExportValueKind.Decimal),
                        new ExportColumn("Flag", ExportValueKind.Boolean),
                        new ExportColumn("Broken", ExportValueKind.Integer),
                        new ExportColumn("NotFinite", ExportValueKind.Decimal),
                    },
                    new object?[][] { new object?[] { 42, 1.5m, true, "not a number", double.NaN } }),
            });

            using var document = Open(bytes);
            var cells = Cells(document, 0).Where(c => c.CellReference!.Value!.EndsWith("2", StringComparison.Ordinal)).ToList();

            Assert.Equal(CellValues.Number, cells[0].DataType!.Value);
            Assert.Equal("42", cells[0].CellValue!.Text);
            Assert.Equal(CellValues.Number, cells[1].DataType!.Value);
            Assert.Equal("1.5", cells[1].CellValue!.Text);
            Assert.Equal(CellValues.Boolean, cells[2].DataType!.Value);
            Assert.Equal("1", cells[2].CellValue!.Text);

            // The promise was Integer; what arrived was text. A text cell is lossless and visibly wrong - a
            // numeric cell holding "not a number" is a workbook Excel refuses to open.
            Assert.Equal(CellValues.InlineString, cells[3].DataType!.Value);
            Assert.Equal("not a number", cells[3].InnerText);

            // SpreadsheetML has no spelling for NaN, so it cannot be a number cell either.
            Assert.Equal(CellValues.InlineString, cells[4].DataType!.Value);
        }

        [Fact]
        public async Task A_value_of_the_WRONG_kind_that_would_format_as_a_number_is_still_TEXT()
        {
            // The cases the kind check exists for. "not a number" is caught anyway because it cannot be formatted
            // as one - but a DateTime formats perfectly well, and written into a numeric cell it is a workbook
            // Excel refuses to open; an int in a Boolean column is a cast that throws mid-render and fails an
            // export over one bad row.
            var moment = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
            var bytes = await RenderAsync(new[]
            {
                Table("t",
                    new[]
                    {
                        new ExportColumn("Count", ExportValueKind.Integer),
                        new ExportColumn("Ratio", ExportValueKind.Decimal),
                        new ExportColumn("Flag", ExportValueKind.Boolean),
                    },
                    new object?[][] { new object?[] { moment, Guid.Empty, 1 } }),
            });

            using var document = Open(bytes);
            var cells = Cells(document, 0).Skip(3).ToList();

            Assert.All(cells, c => Assert.Equal(CellValues.InlineString, c.DataType!.Value));
            Assert.Equal("2026-09-20T12:00:00.0000000Z", cells[0].InnerText);
            Assert.Equal(Guid.Empty.ToString(), cells[1].InnerText);
            Assert.Equal("1", cells[2].InnerText);
            Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document));
        }

        [Fact]
        public async Task A_number_is_written_culture_independently()
        {
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                // sv-SE writes 1,5 - which in a numeric cell is a corrupt workbook, on exactly the owner's laptop.
                Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");

                var bytes = await RenderAsync(new[]
                {
                    Table("t", new[] { new ExportColumn("Ratio", ExportValueKind.Decimal) },
                        new object?[][] { new object?[] { 1.5m }, new object?[] { -0.25d } }),
                });

                using var document = Open(bytes);
                var values = Cells(document, 0).Skip(1).Select(c => c.CellValue!.Text).ToList();
                Assert.Equal(new[] { "1.5", "-0.25" }, values);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        // ---- nothing executes ------------------------------------------------------------------------------

        [Theory]
        [InlineData("=1+1")]
        [InlineData("+1")]
        [InlineData("-1")]
        [InlineData("@SUM(A1)")]
        [InlineData("\tleading tab")]
        [InlineData("\rleading cr")]
        public async Task A_cell_a_spreadsheet_would_execute_is_neutralised_and_is_never_a_formula(string dangerous)
        {
            var bytes = await RenderAsync(new[]
            {
                Table("t", new[] { new ExportColumn("Value", ExportValueKind.Text) }, new object?[][] { new object?[] { dangerous } }),
            });

            using var document = Open(bytes);
            var cell = Cells(document, 0).Last();

            Assert.Equal(CellValues.InlineString, cell.DataType!.Value);
            Assert.StartsWith("'", cell.InnerText);
            Assert.Equal(0, FormulaCount(document));
        }

        [Fact]
        public async Task A_formula_in_a_DECIMAL_column_a_FORMATTER_result_and_a_column_NAME_are_all_neutralised()
        {
            var bytes = await RenderAsync(
                new[]
                {
                    Table("t",
                        new[] { new ExportColumn("=HEADER()", ExportValueKind.Decimal) },
                        new object?[][] { new object?[] { "=cmd|'/c calc'!A1" } }),
                });

            using (var document = Open(bytes))
            {
                var texts = Cells(document, 0).Select(c => c.InnerText).ToList();
                Assert.Equal(new[] { "'=HEADER()", "'=cmd|'/c calc'!A1" }, texts);
                Assert.All(Cells(document, 0), c => Assert.Equal(CellValues.InlineString, c.DataType!.Value));
                Assert.Equal(0, FormulaCount(document));
            }

            // A formatter runs AFTER the value has been inspected, and even over a genuinely numeric value its
            // output is a string - so it is a text cell, neutralised.
            bytes = await RenderAsync(
                new[] { Table("t", new[] { new ExportColumn("Amount", ExportValueKind.Decimal) }, new object?[][] { new object?[] { 5m } }) },
                formatter: new FormulaFormatter());

            using (var document = Open(bytes))
            {
                var cell = Cells(document, 0).Last();
                Assert.Equal(CellValues.InlineString, cell.DataType!.Value);
                Assert.Equal("'=EVIL()", cell.InnerText);
            }
        }

        [Fact]
        public async Task With_neutralisation_OFF_the_text_is_unchanged_and_is_STILL_not_a_formula()
        {
            // Two layers, and this pins that they are separate: the opt-out removes the apostrophe, and the value
            // is nonetheless an inline string. No option turns data into a formula cell.
            var bytes = await RenderAsync(
                new[] { Table("t", new[] { new ExportColumn("Value", ExportValueKind.Text) }, new object?[][] { new object?[] { "=1+1" } }) },
                new ExportRenderOptions { NeutraliseFormulas = false });

            using var document = Open(bytes);
            var cell = Cells(document, 0).Last();

            Assert.Equal("=1+1", cell.InnerText);
            Assert.Equal(CellValues.InlineString, cell.DataType!.Value);
            Assert.Equal(0, FormulaCount(document));
        }

        [Fact]
        public void The_formula_scanner_DOES_find_a_planted_formula()
        {
            using var stream = new MemoryStream();
            using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook))
            {
                var workbookPart = document.AddWorkbookPart();
                var sheetPart = workbookPart.AddNewPart<WorksheetPart>();
                sheetPart.Worksheet = new Worksheet(new SheetData(new Row(
                    new Cell { CellReference = "A1", CellFormula = new CellFormula("1+1") })));
                workbookPart.Workbook = new Workbook(new Sheets(
                    new Sheet { Name = "s", SheetId = 1, Id = workbookPart.GetIdOfPart(sheetPart) }));
            }

            using var reopened = Open(stream.ToArray());
            Assert.Equal(1, FormulaCount(reopened));
        }

        // ---- characters XML cannot carry -------------------------------------------------------------------

        [Fact]
        public async Task Characters_XML_cannot_carry_are_dropped_and_the_workbook_still_opens()
        {
            // Legal in a database column, illegal in XML 1.0. Left in, one such row either throws mid-render or
            // produces a file Excel calls corrupt long after the export was reported Ready.
            var hostile = "a\u0000b\u0008c\u001Fd￾e\uD800f";
            var bytes = await RenderAsync(new[]
            {
                Table("t", new[] { new ExportColumn("bad\u0001name", ExportValueKind.Text) },
                    new object?[][] { new object?[] { hostile }, new object?[] { "tab\there\nnewline \U0001F600 kept" } }),
            });

            using var document = Open(bytes);
            var texts = Cells(document, 0).Select(c => c.InnerText).ToList();
            _output.WriteLine(string.Join(" | ", texts));

            Assert.Equal("badname", texts[0]);
            Assert.Equal("abcdef", texts[1]);
            // Tab, newline and a PAIRED surrogate are legal and survive.
            Assert.Equal("tab\there\nnewline \U0001F600 kept", texts[2]);
            Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document));
        }

        // ---- sheet names -----------------------------------------------------------------------------------

        [Fact]
        public async Task Sheet_names_are_made_acceptable_and_unique()
        {
            var column = new[] { new ExportColumn("A", ExportValueKind.Text) };
            var bytes = await RenderAsync(new[]
            {
                Table("Q3 [draft]: a/b\\c*?", column, Array.Empty<object?[]>()),
                Table("  ", column, Array.Empty<object?[]>()),
                Table("same", column, Array.Empty<object?[]>()),
                Table("SAME", column, Array.Empty<object?[]>()),
                Table(new string('x', 40), column, Array.Empty<object?[]>()),
                Table(new string('x', 40), column, Array.Empty<object?[]>()),
                Table("'quoted'", column, Array.Empty<object?[]>()),
            });

            using var document = Open(bytes);
            var names = SheetNames(document);
            _output.WriteLine(string.Join(" | ", names));

            Assert.Equal("Q3 draft abc", names[0]);
            Assert.Equal("Sheet2", names[1]);
            Assert.Equal("same", names[2]);
            // Excel compares sheet names case-insensitively, so "SAME" collides with "same".
            Assert.Equal("SAME_2", names[3]);
            Assert.Equal(new string('x', 31), names[4]);
            Assert.Equal(new string('x', 29) + "_2", names[5]);
            Assert.Equal("quoted", names[6]);
            Assert.All(names, n => Assert.InRange(n.Length, 1, 31));
            Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document));
        }

        // Found by reconciling the predecessor platform's cases by name (BL-85 residue 7): the several-sheet case above renders EMPTY
        // tables, so nothing asserted that a table's rows land on its OWN sheet.
        [Fact]
        public async Task Each_table_lands_on_a_sheet_of_its_own_with_its_own_rows()
        {
            var bytes = await RenderAsync(new[]
            {
                Table("first", new[] { new ExportColumn("A", ExportValueKind.Text) }, new[] { new object?[] { "one" } }),
                Table("second", new[] { new ExportColumn("B", ExportValueKind.Text) }, new[] { new object?[] { "two" }, new object?[] { "three" } }),
            });

            using var document = Open(bytes);

            Assert.Equal(new[] { "first", "second" }, SheetNames(document));
            Assert.Equal(new[] { "A", "one" }, Cells(document, 0).Select(c => c.InnerText));
            Assert.Equal(new[] { "B", "two", "three" }, Cells(document, 1).Select(c => c.InnerText));
            Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document));
        }

        // ---- shape and limits ------------------------------------------------------------------------------

        [Fact]
        public async Task The_header_can_be_left_out_and_the_row_count_excludes_it()
        {
            var table = Table("t", new[] { new ExportColumn("A", ExportValueKind.Integer) },
                Enumerable.Range(0, 250).Select(i => new object?[] { i }).ToArray());

            using var sink = new MemoryStream();
            var outcome = await new XlsxTabularRenderer().RenderAsync(
                new StubDocument(new[] { table }), sink, new ExportRenderOptions { IncludeHeader = false }, new PassThroughFormatter());

            Assert.Equal(250, outcome.RowCount);
            using var document = Open(sink.ToArray());
            Assert.Equal(250, Cells(document, 0).Count);
            Assert.Equal("A1", Cells(document, 0)[0].CellReference!.Value);
        }

        [Fact]
        public async Task Column_references_run_past_Z_correctly()
        {
            var columns = Enumerable.Range(0, 704).Select(i => new ExportColumn("c" + i, ExportValueKind.Integer)).ToArray();
            var bytes = await RenderAsync(
                new[] { Table("wide", columns, Array.Empty<object?[]>()) });

            using var document = Open(bytes);
            var references = Cells(document, 0).Select(c => c.CellReference!.Value!).ToList();

            Assert.Equal("A1", references[0]);
            Assert.Equal("Z1", references[25]);
            Assert.Equal("AA1", references[26]);
            Assert.Equal("AZ1", references[51]);
            Assert.Equal("BA1", references[52]);
            Assert.Equal("ZZ1", references[701]);
            Assert.Equal("AAA1", references[702]);
            Assert.Empty(new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document));
        }

        [Fact]
        public async Task A_table_longer_than_a_sheet_FAILS_rather_than_writing_a_file_Excel_refuses()
        {
            // 1,048,576 rows including the header is Excel's limit; one more is a corrupt workbook.
            var table = new GeneratedTable(XlsxTabularRenderer.MaximumRowsPerSheet);

            var error = await Assert.ThrowsAsync<ExportRenderException>(() => new XlsxTabularRenderer().RenderAsync(
                new StubDocument(new ITabularTable[] { table }), Stream.Null.AsForwardOnly(), ExportRenderOptions.Default, new PassThroughFormatter()));

            _output.WriteLine($"[row limit] {error.Message}");
            Assert.Equal(XlsxTabularRenderer.MaximumRowsPerSheet, table.Produced);
        }

        [Fact]
        public async Task Rows_are_PULLED_one_at_a_time_and_cancellation_stops_the_render()
        {
            var table = new GeneratedTable(5000);
            await new XlsxTabularRenderer().RenderAsync(
                new StubDocument(new ITabularTable[] { table }), new ForwardOnlySink(), ExportRenderOptions.Default, new PassThroughFormatter());

            Assert.Equal(5000, table.Produced);
            Assert.True(table.MaxOutstanding <= 1, $"the renderer held {table.MaxOutstanding} rows at once");

            using var cts = new CancellationTokenSource();
            var endless = new GeneratedTable(1_000_000);
            var render = new XlsxTabularRenderer().RenderAsync(
                new StubDocument(new ITabularTable[] { endless }), new ForwardOnlySink(), ExportRenderOptions.Default,
                new PassThroughFormatter(), cts.Token);

            cts.CancelAfter(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => render);
            Assert.True(endless.Produced < 1_000_000, "the render consumed the whole table despite cancellation");
        }

        // ---- helpers ---------------------------------------------------------------------------------------

        private static async Task<byte[]> RenderAsync(
            IReadOnlyList<ITabularTable> tables, ExportRenderOptions? options = null, IExportCellFormatter? formatter = null)
        {
            var sink = new ForwardOnlySink();
            await new XlsxTabularRenderer().RenderAsync(
                new StubDocument(tables), sink, options ?? ExportRenderOptions.Default, formatter ?? new PassThroughFormatter());
            return sink.ToArray();
        }

        private static SpreadsheetDocument Open(byte[] bytes) => SpreadsheetDocument.Open(new MemoryStream(bytes), false);

        private static List<string> SheetNames(SpreadsheetDocument document) =>
            document.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().Select(s => s.Name!.Value!).ToList();

        private static List<Cell> Cells(SpreadsheetDocument document, int sheetIndex)
        {
            var sheet = document.WorkbookPart!.Workbook.Sheets!.Elements<Sheet>().ElementAt(sheetIndex);
            var part = (WorksheetPart)document.WorkbookPart.GetPartById(sheet.Id!.Value!);
            return part.Worksheet.Descendants<Cell>().ToList();
        }

        private static int FormulaCount(SpreadsheetDocument document) =>
            document.WorkbookPart!.WorksheetParts.Sum(p => p.Worksheet.Descendants<CellFormula>().Count());

        private static ITabularTable Table(string name, ExportColumn[] columns, object?[][] rows) =>
            new StubTable(name, columns, rows);

        private sealed class PassThroughFormatter : IExportCellFormatter
        {
            public string? Format(object? value, ExportColumn column) => null;
        }

        private sealed class FormulaFormatter : IExportCellFormatter
        {
            public string? Format(object? value, ExportColumn column) => "=EVIL()";
        }

        private sealed class StubDocument : ITabularDocument
        {
            private readonly IReadOnlyList<ITabularTable> _tables;

            public StubDocument(IReadOnlyList<ITabularTable> tables) => _tables = tables;

            public async IAsyncEnumerable<ITabularTable> ReadTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                foreach (var table in _tables)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return table;
                    await Task.Yield();
                }
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        private sealed class StubTable : ITabularTable
        {
            private readonly object?[][] _rows;

            public StubTable(string name, ExportColumn[] columns, object?[][] rows)
            {
                Name = name;
                Columns = columns;
                _rows = rows;
            }

            public string Name { get; }

            public IReadOnlyList<ExportColumn> Columns { get; }

            public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                foreach (var row in _rows)
                {
                    ct.ThrowIfCancellationRequested();
                    yield return row;
                    await Task.Yield();
                }
            }
        }

        /// <summary>Generates rows on demand, counting how many the consumer holds at once.</summary>
        private sealed class GeneratedTable : ITabularTable
        {
            private readonly int _rowCount;
            private int _outstanding;

            public GeneratedTable(int rowCount) => _rowCount = rowCount;

            public string Name => "generated";

            public IReadOnlyList<ExportColumn> Columns { get; } = new[] { new ExportColumn("A", ExportValueKind.Integer) };

            public int Produced { get; private set; }

            public int MaxOutstanding { get; private set; }

            public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                for (var i = 0; i < _rowCount; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    _outstanding++;
                    MaxOutstanding = Math.Max(MaxOutstanding, _outstanding);
                    Produced++;

                    yield return new object?[] { i };

                    _outstanding--;
                    if (i % 1000 == 0)
                    {
                        await Task.Yield();
                    }
                }
            }
        }
    }

    /// <summary>A destination that accepts writes and nothing else - the shape of the artifact's counting stream.</summary>
    internal sealed class ForwardOnlySink : Stream
    {
        private readonly Stream _inner;

        public ForwardOnlySink()
            : this(new MemoryStream())
        {
        }

        public ForwardOnlySink(Stream inner) => _inner = inner;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => ((MemoryStream)_inner).ToArray();

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    }

    internal static class ForwardOnlySinkExtensions
    {
        public static Stream AsForwardOnly(this Stream inner) => new ForwardOnlySink(inner);
    }
}
