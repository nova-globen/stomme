#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Export;
using Stomme.Modules.DataExport.Rendering;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The hand-written PDF renderer: that the file is structurally a PDF, that a cell cannot
    /// write operators of its own, that what Latin-1 cannot show is shown as <c>?</c>, and that it streams.
    /// </summary>
    /// <remarks>
    /// There is no PDF library here to read the artifact back with - which is the point of the renderer - so the
    /// structure is checked the way a reader resolves it: <c>startxref</c> must point at the <c>xref</c> table, and
    /// every offset in that table must land exactly on the <c>n 0 obj</c> it claims. An off-by-one anywhere in the
    /// byte accounting breaks precisely that, and a reader reports it as a damaged file.
    /// </remarks>
    public class PdfRendererTests
    {
        private static readonly Encoding Latin1 = Encoding.Latin1;

        private readonly ITestOutputHelper _output;

        public PdfRendererTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task The_cross_reference_table_points_at_every_object_exactly()
        {
            var pdf = await RenderAsync(new[] { Table("Orders", 3, 120), Table("Second", 2, 5) });
            var text = Latin1.GetString(pdf);

            Assert.StartsWith("%PDF-1.4\n", text);
            Assert.EndsWith("%%EOF\n", text);

            var startxref = int.Parse(Regex.Match(text, @"startxref\n(\d+)\n%%EOF\n$").Groups[1].Value);
            Assert.Equal("xref\n", text.Substring(startxref, 5));

            var header = Regex.Match(text.Substring(startxref), @"^xref\n0 (\d+)\n");
            var count = int.Parse(header.Groups[1].Value);
            var entries = Regex.Matches(text.Substring(startxref + header.Length), @"(\d{10}) (\d{5}) ([nf]) \n").Take(count).ToList();
            Assert.Equal(count, entries.Count);
            Assert.Equal("f", entries[0].Groups[3].Value);

            for (var number = 1; number < count; number++)
            {
                var offset = int.Parse(entries[number].Groups[1].Value);
                var expected = $"{number} 0 obj\n";
                Assert.True(offset > 0, $"object {number} was never written (offset 0)");
                Assert.True(text.Substring(offset, expected.Length) == expected,
                    $"the xref entry for object {number} points at '{text.Substring(offset, Math.Min(12, text.Length - offset))}'");
            }

            Assert.Contains($"/Size {count} /Root 1 0 R", text);
            _output.WriteLine($"[pdf] {pdf.Length:N0} bytes, {count - 1} objects, all xref offsets exact");
        }

        [Fact]
        public async Task Every_stream_LENGTH_is_the_byte_count_of_its_stream()
        {
            // /Length is an indirect object written AFTER the stream it measures - the thing that lets the destination
            // be forward-only. If it disagreed with the bytes, a reader would cut the page short or read past it.
            var text = Latin1.GetString(await RenderAsync(new[] { Table("t", 2, 60) }));

            var streams = Regex.Matches(text, @"(\d+) 0 obj\n<< /Length (\d+) 0 R >>\nstream\n(.*?)\nendstream", RegexOptions.Singleline);
            Assert.NotEmpty(streams);
            foreach (Match stream in streams)
            {
                var declared = Regex.Match(text, $@"\n{stream.Groups[2].Value} 0 obj\n(\d+)\nendobj");
                Assert.True(declared.Success, $"length object {stream.Groups[2].Value} is missing");
                Assert.Equal(stream.Groups[3].Value.Length, int.Parse(declared.Groups[1].Value));
            }
        }

        [Fact]
        public async Task The_page_tree_counts_the_pages_and_a_long_table_repeats_its_header()
        {
            var text = Latin1.GetString(await RenderAsync(new[] { Table("Long", 2, 200) }));

            var pages = Regex.Matches(text, @"/Type /Page /Parent 2 0 R").Count;
            Assert.True(pages >= 3, $"200 rows fitted on {pages} page(s)");
            Assert.Contains($"/Type /Pages /Count {pages} ", text);
            Assert.Equal(pages, Regex.Match(text, @"/Kids \[ ((?:\d+ 0 R )+)\]").Groups[1].Value.Split(" 0 R ", StringSplitOptions.RemoveEmptyEntries).Length);

            // The header is drawn in the bold font, once per page.
            Assert.Equal(pages, Regex.Matches(text, @"BT /F2 8 Tf [\d.]+ [\d.]+ Td \(C0\) Tj ET").Count);
            // And every row made it: the last one is there.
            Assert.Contains("(r199c0) Tj", text);
        }

        [Fact]
        public async Task A_cell_cannot_close_its_string_and_write_operators_of_its_own()
        {
            // An unescaped ')' ends the literal string; what follows would be read as content-stream operators, so a
            // value could draw over the page. Every '(' , ')' and '\' must arrive escaped - and the backslash first,
            // or escaping the parentheses would itself be undone by a value ending in '\'.
            var hostile = @"x) Tj ET BT /F2 40 Tf 0 0 Td (PWNED) Tj ET BT (\";
            var text = Latin1.GetString(await RenderAsync(new[]
            {
                new StubTable("t", new[] { new ExportColumn("A(1)\\", ExportValueKind.Text) }, new[] { new object?[] { hostile } }),
            }, new ExportRenderOptions { PdfOrientation = "Landscape", PdfPageSize = "A3" }));

            // Exactly the text-showing operators the renderer wrote: a title, a header, one cell. No fourth.
            Assert.Equal(3, Regex.Matches(text, @"\) Tj ET\n").Count);
            // The hostile operators are all still INSIDE one literal string: escaped, they never leave it.
            Assert.Contains(@"(x\) Tj ET BT /F2 40 Tf 0 0 Td \(PWNED\) Tj ET BT \(\\)", text, StringComparison.Ordinal);
            Assert.Contains(@"(A\(1\)\\) Tj", text);
        }

        // Found by reconciling the earlier cases by name: the page options were accepted, stored and
        // canonicalised, and nothing asserted that the page actually changes.
        [Theory]
        [InlineData("A4", "Portrait", "595.28", "841.89")]
        [InlineData("Letter", "Portrait", "612", "792")]
        [InlineData("A4", "Landscape", "841.89", "595.28")]
        [InlineData("Legal", "Portrait", "612", "1008")]
        [InlineData("A3", "Landscape", "1190.55", "841.89")]
        [InlineData("no such size", "no such orientation", "595.28", "841.89")]
        public async Task The_page_is_the_size_and_orientation_asked_for_and_nonsense_falls_back_to_A4_portrait(
            string size, string orientation, string width, string height)
        {
            var text = Latin1.GetString(await RenderAsync(
                new[] { Table("t", 1, 1) }, new ExportRenderOptions { PdfPageSize = size, PdfOrientation = orientation }));

            Assert.Contains($"/MediaBox [0 0 {width} {height}]", text, StringComparison.Ordinal);
        }

        [Fact]
        public void The_escape_doubles_the_backslash_FIRST()
        {
            Assert.Equal(@"a\\b\(c\)d", PdfText.Escape(@"a\b(c)d"));
            // A value ending in a backslash must not swallow the string's closing parenthesis.
            Assert.Equal(@"tail\\", PdfText.Escape(@"tail\"));
        }

        [Fact]
        public void What_the_standard_fonts_cannot_show_becomes_a_question_mark()
        {
            // Visibly wrong, never silently dropped: the reader can see something was there.
            Assert.Equal("café åäö", PdfText.ToWinAnsi("café åäö"));
            Assert.Equal("??? ?", PdfText.ToWinAnsi("日本語 €"));
            Assert.Equal("a b c", PdfText.ToWinAnsi("a\tb\nc"));
            Assert.Equal("x?y", PdfText.ToWinAnsi("x\u0085y"));
            Assert.Same("plain", PdfText.ToWinAnsi("plain"));
        }

        [Fact]
        public void A_cell_wider_than_its_column_is_cut_with_an_ellipsis_and_never_overruns()
        {
            var fitted = PdfText.Fit(new string('W', 200), width: 100, fontSize: 8);

            Assert.EndsWith("...", fitted);
            Assert.True(PdfText.WidthOf(fitted, 8) <= 100, $"the fitted text is {PdfText.WidthOf(fitted, 8):0.0} wide in a 100-wide cell");
            Assert.True(fitted.Length < 200);

            Assert.Equal("short", PdfText.Fit("short", 100, 8));
            Assert.Equal(string.Empty, PdfText.Fit("anything", 0, 8));
            // 'W' is the widest capital: fewer of them fit than 'i'.
            Assert.True(PdfText.Fit(new string('i', 200), 100, 8).Length > fitted.Length);
        }

        [Fact]
        public async Task It_renders_into_a_forward_only_sink_pulling_one_row_at_a_time()
        {
            var table = new CountingTable(2000);
            var sink = new ForwardOnlySink();

            var outcome = await new PdfTabularRenderer().RenderAsync(
                new StubDocument(new ITabularTable[] { table }), sink, ExportRenderOptions.Default, new PassThroughFormatter());

            Assert.Equal(2000, outcome.RowCount);
            Assert.True(table.MaxOutstanding <= 1, $"the renderer held {table.MaxOutstanding} rows at once");
            Assert.EndsWith("%%EOF\n", Latin1.GetString(sink.ToArray()));
        }

        [Fact]
        public async Task An_empty_document_is_one_blank_page_and_cancellation_stops_the_render()
        {
            var text = Latin1.GetString(await RenderAsync(Array.Empty<ITabularTable>()));
            Assert.Contains("/Type /Pages /Count 1 ", text);

            using var cts = new CancellationTokenSource();
            var endless = new CountingTable(5_000_000);
            var render = new PdfTabularRenderer().RenderAsync(
                new StubDocument(new ITabularTable[] { endless }), new ForwardOnlySink(), ExportRenderOptions.Default, new PassThroughFormatter(), cts.Token);
            cts.CancelAfter(TimeSpan.FromMilliseconds(50));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => render);
            Assert.True(endless.Produced < 5_000_000);
        }

        [Fact]
        public async Task A_formula_shaped_value_is_NOT_neutralised_because_nothing_in_a_PDF_executes_it()
        {
            var text = Latin1.GetString(await RenderAsync(new[]
            {
                new StubTable("t", new[] { new ExportColumn("V", ExportValueKind.Text) }, new[] { new object?[] { "=1+1" } }),
            }));

            Assert.Contains("(=1+1) Tj", text);
            Assert.DoesNotContain("('=1+1)", text);
        }

        // ---- helpers ---------------------------------------------------------------------------------------

        private static async Task<byte[]> RenderAsync(IReadOnlyList<ITabularTable> tables, ExportRenderOptions? options = null)
        {
            var sink = new ForwardOnlySink();
            await new PdfTabularRenderer().RenderAsync(new StubDocument(tables), sink, options ?? ExportRenderOptions.Default, new PassThroughFormatter());
            return sink.ToArray();
        }

        private static ITabularTable Table(string name, int columns, int rows) => new StubTable(
            name,
            Enumerable.Range(0, columns).Select(c => new ExportColumn("C" + c, ExportValueKind.Text)).ToArray(),
            Enumerable.Range(0, rows).Select(r => Enumerable.Range(0, columns).Select(c => (object?)$"r{r}c{c}").ToArray()).ToArray());

        private sealed class PassThroughFormatter : IExportCellFormatter
        {
            public string? Format(object? value, ExportColumn column) => null;
        }

        private sealed class StubDocument(IReadOnlyList<ITabularTable> tables) : ITabularDocument
        {
            public async IAsyncEnumerable<ITabularTable> ReadTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                foreach (var table in tables)
                {
                    yield return table;
                }

                await Task.CompletedTask;
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
                }

                await Task.CompletedTask;
            }
        }

        private sealed class CountingTable(int rowCount) : ITabularTable
        {
            private int _outstanding;

            public string Name => "counting";

            public IReadOnlyList<ExportColumn> Columns { get; } = new[] { new ExportColumn("N", ExportValueKind.Integer) };

            public int Produced { get; private set; }

            public int MaxOutstanding { get; private set; }

            public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                for (var i = 0; i < rowCount; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    _outstanding++;
                    MaxOutstanding = Math.Max(MaxOutstanding, _outstanding);
                    Produced++;
                    yield return new object?[] { i };
                    _outstanding--;
                    if (i % 500 == 0)
                    {
                        await Task.Yield();
                    }
                }
            }
        }
    }
}
