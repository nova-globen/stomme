#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Export;
using Stomme.Modules.DataExport.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The CSV and JSON renderers, and the cell rules that make an artifact safe to open.
    /// </summary>
    /// <remarks>
    /// The renderers are reached through the engine's own contract, so these cases also pin the contract's
    /// shape: rows are PULLED, a declared kind is a promise a source can break, and a CSV export is one table.
    /// </remarks>
    public class ExportRendererTests
    {
        private readonly ITestOutputHelper _output;

        public ExportRendererTests(ITestOutputHelper output) => _output = output;

        // ---- formula neutralisation ----------------------------------------------------------------------

        [Theory]
        [InlineData("=1+1")]
        [InlineData("+1")]
        [InlineData("-1")]
        [InlineData("@SUM(A1)")]
        [InlineData("\tleading tab")]
        [InlineData("\rleading cr")]
        public async Task A_cell_a_spreadsheet_would_execute_is_neutralised(string dangerous)
        {
            var csv = await RenderCsvAsync(
                Table("t", new[] { new ExportColumn("Value", ExportValueKind.Text) }, new object?[][] { new object?[] { dangerous } }),
                ExportRenderOptions.Default);

            _output.WriteLine($"[cell] {Escape(dangerous)} -> {Escape(csv)}");

            var line = csv.Split("\r\n")[1];
            Assert.StartsWith("'", line.TrimStart('"'));
        }

        [Fact]
        public async Task A_formula_in_a_DECIMAL_column_is_still_neutralised()
        {
            // The column PROMISED a number and the source handed over a formula string. Trusting the declared
            // kind here - "numbers cannot be formulas" - is exactly how a neutralisation guard gets bypassed.
            var csv = await RenderCsvAsync(
                Table("t", new[] { new ExportColumn("Amount", ExportValueKind.Decimal) }, new object?[][] { new object?[] { "=cmd|'/c calc'!A1" } }),
                ExportRenderOptions.Default);

            _output.WriteLine($"[decimal column] {Escape(csv)}");
            Assert.Contains("'=cmd", csv);
        }

        [Fact]
        public async Task A_FORMATTER_that_returns_a_formula_is_neutralised_too()
        {
            // The formatter is as capable of producing "=..." as the source is, and it runs after the value has
            // been inspected - so a guard that only looked at the raw value would miss this entirely.
            var csv = await RenderCsvAsync(
                Table("t", new[] { new ExportColumn("Value", ExportValueKind.Text) }, new object?[][] { new object?[] { "harmless" } }),
                ExportRenderOptions.Default,
                new FormulaFormatter());

            _output.WriteLine($"[formatter] {Escape(csv)}");
            Assert.Contains("'=EVIL()", csv);
        }

        [Fact]
        public async Task A_column_NAME_is_neutralised_like_any_other_cell()
        {
            var csv = await RenderCsvAsync(
                Table("t", new[] { new ExportColumn("=HEADER()", ExportValueKind.Text) }, Array.Empty<object?[]>()),
                ExportRenderOptions.Default);

            _output.WriteLine($"[header] {Escape(csv)}");
            Assert.StartsWith("'=HEADER()", csv);
        }

        [Fact]
        public async Task A_GENUINE_negative_number_is_not_corrupted_with_an_apostrophe()
        {
            // Found by exporting a real event log, whose UserId of -1 came out as '-1. A value whose runtime type IS a
            // number cannot carry a formula - its text is generated here - and prefixing it turns a numeric column
            // into text for whoever sums it.
            var csv = await RenderCsvAsync(
                Table("t",
                    new[]
                    {
                        new ExportColumn("Id", ExportValueKind.Integer),
                        new ExportColumn("Amount", ExportValueKind.Decimal),
                        new ExportColumn("Ratio", ExportValueKind.Decimal),
                    },
                    new object?[][] { new object?[] { -1, -12.5m, -0.25d } }),
                ExportRenderOptions.Default);

            _output.WriteLine($"[numbers] {Escape(csv)}");
            Assert.Contains("\r\n-1,-12.5,-0.25\r\n", csv);
            Assert.DoesNotContain("'", csv);
        }

        [Fact]
        public async Task But_a_STRING_that_looks_like_a_number_is_and_so_is_anything_a_number_cannot_be()
        {
            // The exemption is for the runtime TYPE, never the look of the text: "-1" as a string in an Integer column
            // is a mismatch, and the same path would carry "-1+cmd|...". An infinity is not a finite number; a genuine
            // number in a TEXT column is a mismatch too; and a formatter's output is a string whatever it was given.
            var csv = await RenderCsvAsync(
                Table("t",
                    new[]
                    {
                        new ExportColumn("AsString", ExportValueKind.Integer),
                        new ExportColumn("Payload", ExportValueKind.Integer),
                        new ExportColumn("Infinite", ExportValueKind.Decimal),
                        new ExportColumn("NumberInTextColumn", ExportValueKind.Text),
                    },
                    new object?[][] { new object?[] { "-1", "-1+cmd|' /C calc'!A0", double.NegativeInfinity, -7 } }),
                ExportRenderOptions.Default);

            _output.WriteLine($"[look-alikes] {Escape(csv)}");
            var cells = csv.Split("\r\n")[1];
            Assert.StartsWith("'-1,", cells);
            Assert.Contains("'-1+cmd|", cells);
            Assert.Contains(",'-Infinity,", cells);
            Assert.EndsWith(",'-7", cells);

            var formatted = await RenderCsvAsync(
                Table("t", new[] { new ExportColumn("Amount", ExportValueKind.Decimal) }, new object?[][] { new object?[] { -5m } }),
                ExportRenderOptions.Default,
                new FormulaFormatter());
            Assert.Contains("'=EVIL()", formatted);
        }

        [Fact]
        public async Task With_neutralisation_OFF_the_value_is_written_through_unchanged()
        {
            // The other direction: the guard must actually be the thing doing the work, or the tests above pass
            // for some unrelated reason. Turning it off is a privileged act enforced in the handler, not here.
            var csv = await RenderCsvAsync(
                Table("t", new[] { new ExportColumn("Value", ExportValueKind.Text) }, new object?[][] { new object?[] { "=1+1" } }),
                new ExportRenderOptions { NeutraliseFormulas = false });

            _output.WriteLine($"[off] {Escape(csv)}");
            Assert.DoesNotContain("'=1+1", csv);
            Assert.Contains("=1+1", csv);
        }

        // ---- CSV shape ------------------------------------------------------------------------------------

        [Theory]
        [InlineData(',')]
        [InlineData(';')]
        [InlineData('\t')]
        [InlineData('|')]
        public async Task Every_allowed_delimiter_renders(char delimiter)
        {
            var csv = await RenderCsvAsync(
                Table("t", new[] { new ExportColumn("A", ExportValueKind.Text), new ExportColumn("B", ExportValueKind.Text) },
                    new object?[][] { new object?[] { "1", "2" } }),
                new ExportRenderOptions { CsvDelimiter = delimiter });

            Assert.Contains("1" + delimiter + "2", csv);
        }

        [Theory]
        [InlineData('\'')]
        [InlineData('"')]
        [InlineData('x')]
        [InlineData('\n')]
        public async Task A_delimiter_outside_the_allowlist_is_refused(char delimiter)
        {
            // An arbitrary caller-chosen delimiter lets a requester pick one that occurs in the data and change
            // how every downstream consumer parses the file.
            var error = await Assert.ThrowsAsync<ExportRenderException>(() => RenderCsvAsync(
                Table("t", new[] { new ExportColumn("A", ExportValueKind.Text) }, Array.Empty<object?[]>()),
                new ExportRenderOptions { CsvDelimiter = delimiter }));

            _output.WriteLine($"[refused '{Escape(delimiter.ToString())}'] {error.Message}");
        }

        [Fact]
        public async Task A_field_containing_the_delimiter_a_quote_or_a_newline_is_quoted()
        {
            var csv = await RenderCsvAsync(
                Table("t", new[] { new ExportColumn("A", ExportValueKind.Text) },
                    new object?[][]
                    {
                        new object?[] { "has,comma" },
                        new object?[] { "has\"quote" },
                        new object?[] { "has\nnewline" },
                        new object?[] { "plain" },
                    }),
                ExportRenderOptions.Default);

            _output.WriteLine(Escape(csv));
            Assert.Contains("\"has,comma\"", csv);
            Assert.Contains("\"has\"\"quote\"", csv);
            Assert.Contains("\"has\nnewline\"", csv);
            // The one that needs nothing is not quoted - or every field would be.
            Assert.Contains("\r\nplain\r\n", csv);
        }

        [Fact]
        public async Task CSV_fails_when_a_second_table_appears_despite_the_declaration()
        {
            // Request validation rejects CSV for a source DECLARING Multiple. This is the other half: a source
            // that declared Single and produced two tables anyway must fail the render, not write a file whose
            // columns change halfway down.
            var document = new StubDocument(new[]
            {
                Table("first", new[] { new ExportColumn("A", ExportValueKind.Text) }, new object?[][] { new object?[] { "1" } }),
                Table("second", new[] { new ExportColumn("B", ExportValueKind.Text) }, new object?[][] { new object?[] { "2" } }),
            });

            using var sink = new MemoryStream();
            var error = await Assert.ThrowsAsync<ExportRenderException>(() =>
                new CsvTabularRenderer().RenderAsync(document, sink, ExportRenderOptions.Default, new PassThroughFormatter()));

            _output.WriteLine($"[two tables] {error.Message}");
        }

        [Fact]
        public async Task The_row_count_is_what_was_written()
        {
            var document = new StubDocument(new[]
            {
                Table("t", new[] { new ExportColumn("A", ExportValueKind.Text) },
                    Enumerable.Range(0, 250).Select(i => new object?[] { i.ToString() }).ToArray()),
            });

            using var sink = new MemoryStream();
            var outcome = await new CsvTabularRenderer().RenderAsync(
                document, sink, ExportRenderOptions.Default, new PassThroughFormatter());

            Assert.Equal(250, outcome.RowCount);
        }

        // ---- lazily pulled, not materialised ---------------------------------------------------------------

        [Fact]
        public async Task Rows_are_PULLED_one_at_a_time_rather_than_materialised()
        {
            // The engine's central promise: a million-row export never exists in memory at once. A source that
            // built a list before returning would defeat it silently, so the contract is asserted on the pull
            // itself - at no point has the renderer asked for more than one row beyond the one it is writing.
            var table = new CountingTable(
                new[] { new ExportColumn("A", ExportValueKind.Integer) }, rowCount: 5000);

            using var sink = new MemoryStream();
            await new CsvTabularRenderer().RenderAsync(
                new StubDocument(new ITabularTable[] { table }), sink, ExportRenderOptions.Default, new PassThroughFormatter());

            _output.WriteLine($"[pull] max rows outstanding at once: {table.MaxOutstanding}");
            Assert.Equal(5000, table.Produced);
            Assert.True(table.MaxOutstanding <= 1,
                $"the renderer held {table.MaxOutstanding} rows at once; it must pull one, write it and let it go");
        }

        [Fact]
        public async Task Cancellation_stops_the_render_between_rows()
        {
            using var cts = new CancellationTokenSource();
            var table = new CountingTable(new[] { new ExportColumn("A", ExportValueKind.Integer) }, rowCount: 1_000_000);

            using var sink = new MemoryStream();
            var render = new CsvTabularRenderer().RenderAsync(
                new StubDocument(new ITabularTable[] { table }), sink, ExportRenderOptions.Default,
                new PassThroughFormatter(), cts.Token);

            cts.CancelAfter(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => render);

            _output.WriteLine($"[cancel] stopped after {table.Produced} of 1,000,000 rows");
            Assert.True(table.Produced < 1_000_000, "the render consumed the whole table despite cancellation");
        }

        // ---- JSON ------------------------------------------------------------------------------------------

        [Fact]
        public async Task JSON_writes_typed_values_and_falls_back_to_a_string_on_a_mismatch()
        {
            var json = await RenderJsonAsync(Table("t",
                new[]
                {
                    new ExportColumn("Count", ExportValueKind.Integer),
                    new ExportColumn("Ratio", ExportValueKind.Decimal),
                    new ExportColumn("Flag", ExportValueKind.Boolean),
                    new ExportColumn("Broken", ExportValueKind.Integer),
                },
                new object?[][] { new object?[] { 42, 1.5m, true, "not a number" } }));

            _output.WriteLine(json);

            using var parsed = JsonDocument.Parse(json);
            var row = parsed.RootElement.GetProperty("tables")[0].GetProperty("rows")[0];

            Assert.Equal(JsonValueKind.Number, row.GetProperty("Count").ValueKind);
            Assert.Equal(JsonValueKind.Number, row.GetProperty("Ratio").ValueKind);
            Assert.Equal(JsonValueKind.True, row.GetProperty("Flag").ValueKind);
            // The promise was Integer; what arrived was text. A string is lossless and visibly wrong - a broken
            // JSON number would be neither.
            Assert.Equal(JsonValueKind.String, row.GetProperty("Broken").ValueKind);
        }

        [Fact]
        public async Task JSON_de_duplicates_repeated_column_names()
        {
            // A JSON object cannot carry the same property twice, and a source may declare two columns alike.
            var json = await RenderJsonAsync(Table("t",
                new[] { new ExportColumn("Name", ExportValueKind.Text), new ExportColumn("Name", ExportValueKind.Text) },
                new object?[][] { new object?[] { "first", "second" } }));

            _output.WriteLine(json);
            using var parsed = JsonDocument.Parse(json);
            var row = parsed.RootElement.GetProperty("tables")[0].GetProperty("rows")[0];

            Assert.Equal("first", row.GetProperty("Name").GetString());
            Assert.Equal("second", row.GetProperty("Name_2").GetString());
        }

        [Fact]
        public async Task JSON_does_NOT_neutralise_because_nothing_executes_it()
        {
            // The guard belongs where the hazard is. A leading apostrophe here would corrupt the value for every
            // legitimate consumer of the file.
            var json = await RenderJsonAsync(Table("t",
                new[] { new ExportColumn("Value", ExportValueKind.Text) },
                new object?[][] { new object?[] { "=1+1" } }));

            using var parsed = JsonDocument.Parse(json);
            Assert.Equal("=1+1", parsed.RootElement.GetProperty("tables")[0].GetProperty("rows")[0].GetProperty("Value").GetString());
        }

        // ---- three options nothing here exercised -------

        [Fact]
        public async Task CSV_can_leave_the_header_out_and_the_row_count_is_unchanged()
        {
            var table = Table("t", new[] { new ExportColumn("OnlyInTheHeader", ExportValueKind.Text) }, new object?[][] { new object?[] { "a" }, new object?[] { "b" } });

            var with = await RenderCsvAsync(table, ExportRenderOptions.Default);
            var without = await RenderCsvAsync(table, new ExportRenderOptions { IncludeHeader = false });

            _output.WriteLine($"[header on] {Escape(with)} [header off] {Escape(without)}");
            Assert.StartsWith("OnlyInTheHeader\r\n", with);
            Assert.DoesNotContain("OnlyInTheHeader", without);
            Assert.Equal("a\r\nb\r\n", without);
        }

        [Fact]
        public async Task JSON_carries_every_table_by_name_in_the_order_the_source_produced_them()
        {
            // Keying a multi-table document by table name lost one of two tables named alike. An array of
            // named tables cannot.
            using var sink = new MemoryStream();
            var outcome = await new JsonTabularRenderer().RenderAsync(
                new StubDocument(new[]
                {
                    Table("same", new[] { new ExportColumn("A", ExportValueKind.Integer) }, new object?[][] { new object?[] { 1 } }),
                    Table("same", new[] { new ExportColumn("B", ExportValueKind.Integer) }, new object?[][] { new object?[] { 2 }, new object?[] { 3 } }),
                }),
                sink, ExportRenderOptions.Default, new PassThroughFormatter());

            using var parsed = JsonDocument.Parse(sink.ToArray());
            var tables = parsed.RootElement.GetProperty("tables");

            Assert.Equal(2, tables.GetArrayLength());
            Assert.Equal("same", tables[0].GetProperty("name").GetString());
            Assert.Equal("same", tables[1].GetProperty("name").GetString());
            Assert.Equal(1, tables[0].GetProperty("rows")[0].GetProperty("A").GetInt32());
            Assert.Equal(3, tables[1].GetProperty("rows")[1].GetProperty("B").GetInt32());
            Assert.Equal(3, outcome.RowCount);
        }

        [Fact]
        public async Task JSON_indentation_changes_the_layout_and_nothing_else()
        {
            var table = Table("t", new[] { new ExportColumn("A", ExportValueKind.Text) }, new object?[][] { new object?[] { "v" } });

            async Task<string> RenderAsync(bool indented)
            {
                using var sink = new MemoryStream();
                await new JsonTabularRenderer().RenderAsync(
                    new StubDocument(new[] { table }), sink, new ExportRenderOptions { JsonIndented = indented }, new PassThroughFormatter());
                return Encoding.UTF8.GetString(sink.ToArray());
            }

            var compact = await RenderAsync(false);
            var indented = await RenderAsync(true);

            Assert.DoesNotContain("\n", compact);
            Assert.Contains("\n", indented);
            Assert.Equal(
                JsonSerializer.Serialize(JsonDocument.Parse(compact).RootElement),
                JsonSerializer.Serialize(JsonDocument.Parse(indented).RootElement));
        }

        // ---- helpers ---------------------------------------------------------------------------------------

        private static async Task<string> RenderCsvAsync(
            ITabularTable table, ExportRenderOptions options, IExportCellFormatter? formatter = null)
        {
            using var sink = new MemoryStream();
            await new CsvTabularRenderer().RenderAsync(
                new StubDocument(new[] { table }), sink, options, formatter ?? new PassThroughFormatter());
            return Encoding.UTF8.GetString(sink.ToArray());
        }

        private static async Task<string> RenderJsonAsync(ITabularTable table)
        {
            using var sink = new MemoryStream();
            await new JsonTabularRenderer().RenderAsync(
                new StubDocument(new[] { table }), sink, ExportRenderOptions.Default, new PassThroughFormatter());
            return Encoding.UTF8.GetString(sink.ToArray());
        }

        private static ITabularTable Table(string name, ExportColumn[] columns, object?[][] rows) =>
            new StubTable(name, columns, rows);

        private static string Escape(string value) =>
            value.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

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

            public async IAsyncEnumerable<ITabularTable> ReadTablesAsync(
                [EnumeratorCancellation] CancellationToken ct = default)
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

        /// <summary>Counts how many rows it has handed out that the consumer has not finished with.</summary>
        private sealed class CountingTable : ITabularTable
        {
            private readonly int _rowCount;
            private int _outstanding;

            public CountingTable(ExportColumn[] columns, int rowCount)
            {
                Columns = columns;
                _rowCount = rowCount;
            }

            public string Name => "counting";

            public IReadOnlyList<ExportColumn> Columns { get; }

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

                    // Control is back here only once the consumer has finished with the row it was given.
                    _outstanding--;
                    await Task.Yield();
                }
            }
        }
    }
}
