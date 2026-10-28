#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Export;

namespace Stomme.Modules.DataExport.Rendering
{
    /// <summary>
    /// PDF 1.4, written by hand, one page at a time (BL-85, D108 item 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No PDF library, on purpose.</b> The permissive ones that lay out tables either pull a font stack this
    /// product denies (SixLabors) or are not permissive at all (QuestPDF, iText); D108 accepts a plain tabular PDF
    /// over the 14 standard fonts instead, and books a Unicode, embedded-font PDF as residue.
    /// </para>
    /// <para>
    /// <b>The honest limit: Latin-1 only.</b> The standard fonts carry no glyphs beyond WinAnsi, so a character
    /// outside it is written as <c>?</c> - visibly, never silently dropped. A cell wider than its column is
    /// truncated with an ellipsis; this is a report to read, not the data of record. CSV, XLSX and JSON are lossless.
    /// </para>
    /// <para>
    /// <b>It streams.</b> the predecessor platform built the whole document in memory and called <c>ToArray()</c>. Here a page's
    /// content is assembled, written and released before the next page begins, and each stream's <c>/Length</c> is an
    /// INDIRECT object written after it - so nothing is ever rewound and the destination may be forward-only. What
    /// is retained across the document is one byte offset per object and one object number per page: a few bytes a
    /// page, not the page.
    /// </para>
    /// <para>
    /// <b>No fetch surface and no injection.</b> Nothing here resolves a URL, an image or a font file. Text enters
    /// the content stream only as a literal string with <c>\</c>, <c>(</c> and <c>)</c> escaped, which is what stops
    /// a cell value from closing the string and writing operators of its own. Nothing in a PDF executes a leading
    /// <c>=</c>, so - as with JSON - values are NOT formula-neutralised: the apostrophe would only corrupt them.
    /// </para>
    /// </remarks>
    internal sealed class PdfTabularRenderer : ITabularRenderer
    {
        private const double Margin = 36;
        private const double FontSize = 8;
        private const double LineHeight = 11;
        private const double CellPadding = 3;

        public ExportFormat Format => ExportFormat.Pdf;

        public async Task<RenderOutcome> RenderAsync(
            ITabularDocument document,
            Stream destination,
            ExportRenderOptions options,
            IExportCellFormatter formatter,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(destination);
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(formatter);

            var (width, height) = PageSize(options);
            var writer = new PdfWriter(destination, width, height);
            await writer.BeginAsync(ct);

            var rows = 0L;
            await foreach (var table in document.ReadTablesAsync(ct).WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();
                rows += await WriteTableAsync(writer, table, options, formatter, width, height, ct);
            }

            await writer.EndAsync(ct);
            return new RenderOutcome(rows);
        }

        private static async Task<long> WriteTableAsync(
            PdfWriter writer,
            ITabularTable table,
            ExportRenderOptions options,
            IExportCellFormatter formatter,
            double pageWidth,
            double pageHeight,
            CancellationToken ct)
        {
            var columns = table.Columns;
            var columnWidth = columns.Count == 0 ? 0 : (pageWidth - (2 * Margin)) / columns.Count;
            var rows = 0L;

            var page = new PageBuilder(pageHeight);
            page.Title(table.Name);

            void Header()
            {
                if (!options.IncludeHeader || columns.Count == 0)
                {
                    return;
                }

                for (var i = 0; i < columns.Count; i++)
                {
                    page.Cell(Margin + (i * columnWidth), columnWidth, columns[i].Name, bold: true);
                }

                page.NextLine();
            }

            Header();

            await foreach (var row in table.ReadRowsAsync(ct).WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();

                if (page.IsFull)
                {
                    await writer.WritePageAsync(page.Content, ct);
                    page = new PageBuilder(pageHeight);
                    Header();
                }

                for (var i = 0; i < columns.Count; i++)
                {
                    var value = i < row.Length ? row[i] : null;
                    page.Cell(Margin + (i * columnWidth), columnWidth, ExportCellText.Format(value, columns[i], formatter), bold: false);
                }

                page.NextLine();
                rows++;
            }

            await writer.WritePageAsync(page.Content, ct);
            return rows;
        }

        private static (double Width, double Height) PageSize(ExportRenderOptions options)
        {
            var (width, height) = options.PdfPageSize?.ToUpperInvariant() switch
            {
                "A3" => (841.89, 1190.55),
                "LETTER" => (612.0, 792.0),
                "LEGAL" => (612.0, 1008.0),
                _ => (595.28, 841.89), // A4
            };

            return string.Equals(options.PdfOrientation, "Landscape", StringComparison.OrdinalIgnoreCase) ? (height, width) : (width, height);
        }

        /// <summary>One page's content stream, assembled and then let go.</summary>
        private sealed class PageBuilder
        {
            private readonly StringBuilder _content = new();
            private double _y;

            public PageBuilder(double pageHeight) => _y = pageHeight - Margin - FontSize;

            public string Content => _content.ToString();

            public bool IsFull => _y < Margin;

            public void Title(string? text)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return;
                }

                Show(Margin, _y, text!, bold: true, size: FontSize + 2);
                _y -= LineHeight * 1.5;
            }

            public void Cell(double x, double width, string text, bool bold) =>
                Show(x, _y, PdfText.Fit(text, width - CellPadding, FontSize), bold, FontSize);

            public void NextLine() => _y -= LineHeight;

            private void Show(double x, double y, string text, bool bold, double size)
            {
                if (text.Length == 0)
                {
                    return;
                }

                _content.Append("BT /").Append(bold ? "F2" : "F1").Append(' ').Append(Number(size)).Append(" Tf ")
                    .Append(Number(x)).Append(' ').Append(Number(y)).Append(" Td (")
                    .Append(PdfText.Escape(text)).Append(") Tj ET\n");
            }
        }

        private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>The file structure: objects out in the order they are made, the index of them at the end.</summary>
        private sealed class PdfWriter
        {
            // Latin-1 maps char 0-255 to the same byte, which is what a PDF literal string in WinAnsi wants.
            private static readonly Encoding Latin1 = Encoding.Latin1;

            private readonly Stream _destination;
            private readonly double _width;
            private readonly double _height;
            private readonly List<long> _offsets = new() { 0 }; // object 0 is the free-list head
            private readonly List<int> _pages = new();
            private long _position;

            // Fixed object numbers, so pages can name their parent before the page tree is written.
            private const int Catalog = 1;
            private const int PageTree = 2;
            private const int Regular = 3;
            private const int Bold = 4;

            public PdfWriter(Stream destination, double width, double height)
            {
                _destination = destination;
                _width = width;
                _height = height;
            }

            public async Task BeginAsync(CancellationToken ct)
            {
                // The second line's high bytes mark the file as binary for transfer tools, as the specification advises.
                await WriteAsync("%PDF-1.4\n%âãÏÓ\n", ct);

                _offsets.AddRange(new long[] { 0, 0, 0, 0 }); // 1-4 reserved; 1 and 2 are written last
                await ObjectAsync(Regular, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>", ct);
                await ObjectAsync(Bold, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>", ct);
            }

            public async Task WritePageAsync(string content, CancellationToken ct)
            {
                var bytes = Latin1.GetBytes(content);

                var stream = Allocate();
                var length = Allocate();
                var page = Allocate();

                _offsets[stream] = _position;
                await WriteAsync($"{stream} 0 obj\n<< /Length {length} 0 R >>\nstream\n", ct);
                await WriteAsync(bytes, ct);
                await WriteAsync("\nendstream\nendobj\n", ct);

                // The length comes AFTER the stream it measures: that is what lets the destination be forward-only.
                await ObjectAsync(length, bytes.Length.ToString(CultureInfo.InvariantCulture), ct);

                await ObjectAsync(
                    page,
                    $"<< /Type /Page /Parent {PageTree} 0 R /MediaBox [0 0 {Number(_width)} {Number(_height)}] " +
                    $"/Resources << /Font << /F1 {Regular} 0 R /F2 {Bold} 0 R >> >> /Contents {stream} 0 R >>",
                    ct);

                _pages.Add(page);
            }

            public async Task EndAsync(CancellationToken ct)
            {
                if (_pages.Count == 0)
                {
                    // A PDF with no pages is not a PDF a reader will open; an empty document is one blank page.
                    await WritePageAsync(string.Empty, ct);
                }

                var kids = new StringBuilder();
                foreach (var page in _pages)
                {
                    kids.Append(page).Append(" 0 R ");
                }

                await ObjectAsync(PageTree, $"<< /Type /Pages /Count {_pages.Count} /Kids [ {kids}] >>", ct);
                await ObjectAsync(Catalog, $"<< /Type /Catalog /Pages {PageTree} 0 R >>", ct);

                var xref = _position;
                var table = new StringBuilder();
                table.Append("xref\n0 ").Append(_offsets.Count).Append('\n');
                table.Append("0000000000 65535 f \n");
                for (var i = 1; i < _offsets.Count; i++)
                {
                    table.Append(_offsets[i].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
                }

                table.Append("trailer\n<< /Size ").Append(_offsets.Count).Append(" /Root ").Append(Catalog).Append(" 0 R >>\n");
                table.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
                await WriteAsync(table.ToString(), ct);
                await _destination.FlushAsync(ct);
            }

            private int Allocate()
            {
                _offsets.Add(0);
                return _offsets.Count - 1;
            }

            private async Task ObjectAsync(int number, string body, CancellationToken ct)
            {
                _offsets[number] = _position;
                await WriteAsync($"{number} 0 obj\n{body}\nendobj\n", ct);
            }

            private Task WriteAsync(string text, CancellationToken ct) => WriteAsync(Latin1.GetBytes(text), ct);

            private async Task WriteAsync(byte[] bytes, CancellationToken ct)
            {
                await _destination.WriteAsync(bytes, ct);
                _position += bytes.Length;
            }
        }
    }

    /// <summary>Text as a PDF literal string in WinAnsi: what survives, what it costs in width, how it is escaped.</summary>
    internal static class PdfText
    {
        // Helvetica advance widths for 32-126, in 1/1000 em (the Adobe AFM). Above that a generous flat width is used:
        // over-estimating only truncates a little early, under-estimating would let a cell run into its neighbour.
        private static readonly short[] Widths =
        {
            278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
            556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556,
            1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778,
            667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556,
            333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556,
            556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584,
        };

        private const short WideFallback = 722;

        private const string Ellipsis = "...";

        /// <summary>
        /// What a standard font can show: WinAnsi. Anything else becomes <c>?</c>; a control character becomes a space.
        /// </summary>
        public static string ToWinAnsi(string text)
        {
            StringBuilder? mapped = null;
            for (var i = 0; i < text.Length; i++)
            {
                var character = text[i];
                var replacement = character switch
                {
                    < ' ' => ' ',
                    // 0x7F-0x9F are controls in Latin-1 and do not mean in WinAnsi what they mean in Unicode.
                    >= '\u007F' and <= '\u009F' => '?',
                    > 'ÿ' => '?',
                    _ => character,
                };

                if (replacement != character)
                {
                    mapped ??= new StringBuilder(text);
                    mapped[i] = replacement;
                }
            }

            return mapped?.ToString() ?? text;
        }

        public static double WidthOf(string text, double fontSize)
        {
            var units = 0;
            foreach (var character in text)
            {
                units += character >= 32 && character <= 126 ? Widths[character - 32] : WideFallback;
            }

            return units / 1000d * fontSize;
        }

        /// <summary>The text, WinAnsi-mapped, cut to fit <paramref name="width"/> with an ellipsis when it must be.</summary>
        public static string Fit(string text, double width, double fontSize)
        {
            var mapped = ToWinAnsi(text);
            if (width <= 0 || WidthOf(mapped, fontSize) <= width)
            {
                return width <= 0 ? string.Empty : mapped;
            }

            var room = width - WidthOf(Ellipsis, fontSize);
            var kept = 0;
            var used = 0d;
            while (kept < mapped.Length)
            {
                var next = WidthOf(mapped[kept].ToString(), fontSize);
                if (used + next > room)
                {
                    break;
                }

                used += next;
                kept++;
            }

            return kept == 0 ? string.Empty : mapped.Substring(0, kept) + Ellipsis;
        }

        /// <summary>
        /// Escapes the three characters that mean something inside a literal string.
        /// </summary>
        /// <remarks>
        /// An unescaped <c>)</c> would CLOSE the string, and whatever followed in the cell would be read as content
        /// stream operators - a value could draw over the page or hide other rows. The backslash is escaped first,
        /// or escaping the parentheses would itself be undone.
        /// </remarks>
        public static string Escape(string text) =>
            text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
    }
}
