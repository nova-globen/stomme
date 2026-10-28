#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Packaging;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Export;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Stomme.Modules.DataExport.Rendering
{
    /// <summary>
    /// XLSX: one sheet per table, rows streamed through the SAX writer (BL-85, D108 refutation 12).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The package is opened WRITE-ONLY, and that is the whole point.</b> The convenient
    /// <c>SpreadsheetDocument.Create(stream)</c> overload opens the underlying package read-WRITE, which puts
    /// the ZIP into update mode - and in update mode each entry's uncompressed bytes are held in memory until
    /// the package is disposed. A million-row sheet would therefore sit in memory in full, and the artifact
    /// size cap (which counts bytes as they reach the destination) would only fire after the damage was done.
    /// Handing <c>SpreadsheetDocument.Create(package)</c> a package opened with <see cref="FileAccess.Write"/>
    /// keeps the ZIP in create mode, where each entry is compressed and written through as it is produced.
    /// <c>XlsxStreamingMeasurementTests</c> is what makes that a measurement rather than a claim.
    /// </para>
    /// <para>
    /// <b>No shared string table.</b> The shared-string optimisation requires every distinct string in the
    /// document to be held until the table can be written, which is exactly the thing this renderer refuses to
    /// do. Text is written as an <c>inlineStr</c> instead - larger on disk, constant in memory.
    /// </para>
    /// <para>
    /// <b>No cell ever carries a formula.</b> No <c>CellFormula</c> element is emitted, so a value that looks
    /// like <c>=cmd|...</c> is data, not a command. Text cells are neutralised on top of that, because a
    /// spreadsheet will re-interpret an inline string the moment a human edits the cell, and because the same
    /// sheet saved as CSV by its recipient would otherwise carry the live formula.
    /// </para>
    /// </remarks>
    internal sealed class XlsxTabularRenderer : ITabularRenderer
    {
        /// <summary>Excel's own limits. Exceeding either produces a file Excel refuses to open.</summary>
        internal const int MaximumRowsPerSheet = 1_048_576;

        internal const int MaximumColumnsPerSheet = 16_384;

        /// <summary>Excel's sheet-name length limit.</summary>
        internal const int MaximumSheetNameLength = 31;

        private const string ForbiddenSheetNameCharacters = "[]:*?/\\";

        private const string WorkbookContentType =
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml";

        private const string WorksheetContentType =
            "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml";

        private const string OfficeDocumentRelationshipType =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";

        private const string WorksheetRelationshipType =
            "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet";

        private static readonly Uri WorkbookLocation = new("/xl/workbook.xml", UriKind.Relative);

        private static readonly ExportRenderOptions NoHeader = new() { IncludeHeader = false };

        public ExportFormat Format => ExportFormat.Xlsx;

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

            // FileMode.Create + FileAccess.Write keeps the ZIP in create mode, so each part is compressed straight
            // into the destination instead of being accumulated for a later update.
            using var package = Package.Open(destination, FileMode.Create, FileAccess.Write);

            var rows = 0L;
            var sheets = new List<(string Name, Uri Location)>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await foreach (var table in document.ReadTablesAsync(ct).WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();

                var location = SheetLocation(sheets.Count + 1);
                var part = package.CreatePart(location, WorksheetContentType, CompressionOption.Normal);

                // One part stream open at a time, closed before the next part is created: a create-mode ZIP
                // writes its entries strictly in sequence.
                await using (var stream = part.GetStream(FileMode.Create, FileAccess.Write))
                {
                    rows += await WriteSheetAsync(stream, table, options, formatter, ct);
                }

                sheets.Add((UniqueSheetName(table.Name, sheets.Count + 1, usedNames), location));
            }

            if (sheets.Count == 0)
            {
                // A workbook with no sheet at all is not a file Excel will open, so an empty document still gets
                // one empty sheet rather than a corrupt artifact.
                var location = SheetLocation(1);
                var part = package.CreatePart(location, WorksheetContentType, CompressionOption.Normal);
                await using (var stream = part.GetStream(FileMode.Create, FileAccess.Write))
                {
                    await WriteSheetAsync(stream, EmptyTable.Instance, NoHeader, formatter, ct);
                }

                sheets.Add(("Sheet1", location));
            }

            // The workbook part is written LAST and is tiny - one <sheet> element per table. Writing it last is
            // what lets the sheets stream: their names are not known until their tables have been pulled.
            WriteWorkbook(package, sheets);
            return new RenderOutcome(rows);
        }

        internal static async Task<long> WriteSheetAsync(
            Stream sheet,
            ITabularTable table,
            ExportRenderOptions options,
            IExportCellFormatter formatter,
            CancellationToken ct)
        {
            var columns = table.Columns;
            if (columns.Count > MaximumColumnsPerSheet)
            {
                throw new ExportRenderException(
                    $"An XLSX sheet holds at most {MaximumColumnsPerSheet} columns and this table declares " +
                    $"{columns.Count}. Export it as CSV or JSON instead.");
            }

            var rows = 0L;

            // OpenXmlWriter is the SAX half of the SDK: it emits elements straight into the stream rather than
            // building a DOM. The DOM equivalent would hold every Cell object until the part was saved.
            using var writer = OpenXmlWriter.Create(sheet);
            writer.WriteStartElement(new Worksheet());
            writer.WriteStartElement(new SheetData());

            var rowIndex = 1u;

            if (options.IncludeHeader)
            {
                writer.WriteStartElement(new Row { RowIndex = rowIndex });
                for (var i = 0; i < columns.Count; i++)
                {
                    // The header is data too: a column NAME can begin with '=' just as a value can.
                    var name = ExportCellText.Neutralise(columns[i].Name, options.NeutraliseFormulas);
                    WriteInlineString(writer, Reference(i, rowIndex), name);
                }

                writer.WriteEndElement();
                rowIndex++;
            }

            await foreach (var row in table.ReadRowsAsync(ct).WithCancellation(ct))
            {
                ct.ThrowIfCancellationRequested();

                if (rowIndex > MaximumRowsPerSheet)
                {
                    throw new ExportRenderException(
                        $"An XLSX sheet holds at most {MaximumRowsPerSheet} rows including the header, and " +
                        "this table produced more. Export it as CSV or JSON instead.");
                }

                writer.WriteStartElement(new Row { RowIndex = rowIndex });

                for (var i = 0; i < columns.Count; i++)
                {
                    var value = i < row.Length ? row[i] : null;
                    if (value is null)
                    {
                        continue;
                    }

                    WriteCell(writer, Reference(i, rowIndex), value, columns[i], options, formatter);
                }

                writer.WriteEndElement();
                rowIndex++;
                rows++;
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.Close();

            return rows;
        }

        /// <summary>
        /// One cell - typed when the runtime value honours its column, text when it does not.
        /// </summary>
        /// <remarks>
        /// <see cref="ExportColumn.Kind"/> is what the source PROMISED. Writing a mismatched value into a
        /// numeric cell would either make Excel refuse the file or, worse, silently change the number; a text
        /// cell is lossless and visibly wrong, which is the right failure mode. A formatter's output is always
        /// text for the same reason - it is a string, whatever the column claimed.
        /// </remarks>
        private static void WriteCell(
            OpenXmlWriter writer,
            string reference,
            object value,
            ExportColumn column,
            ExportRenderOptions options,
            IExportCellFormatter formatter)
        {
            var formatted = formatter.Format(value, column);
            if (formatted != null)
            {
                WriteInlineString(writer, reference, ExportCellText.Neutralise(formatted, options.NeutraliseFormulas));
                return;
            }

            if (ExportCellText.Matches(value, column.Kind))
            {
                switch (column.Kind)
                {
                    case ExportValueKind.Integer:
                    case ExportValueKind.Decimal:
                        if (TryNumber(value, out var number))
                        {
                            WriteTyped(writer, reference, CellValues.Number, number);
                            return;
                        }

                        break;

                    case ExportValueKind.Boolean:
                        WriteTyped(writer, reference, CellValues.Boolean, (bool)value ? "1" : "0");
                        return;
                }
            }

            WriteInlineString(
                writer,
                reference,
                ExportCellText.Neutralise(ExportCellText.Format(value, column, formatter), options.NeutraliseFormulas));
        }

        /// <summary>The value as a number a spreadsheet can hold; false for NaN and the infinities.</summary>
        private static bool TryNumber(object value, out string text)
        {
            switch (value)
            {
                case double number when double.IsNaN(number) || double.IsInfinity(number):
                case float single when float.IsNaN(single) || float.IsInfinity(single):
                    // SpreadsheetML has no spelling for these; "NaN" in a numeric cell is a corrupt file.
                    text = string.Empty;
                    return false;
                case double number:
                    text = number.ToString("R", CultureInfo.InvariantCulture);
                    return true;
                case float single:
                    text = single.ToString("R", CultureInfo.InvariantCulture);
                    return true;
                case IFormattable formattable:
                    text = formattable.ToString(null, CultureInfo.InvariantCulture);
                    return true;
                default:
                    text = string.Empty;
                    return false;
            }
        }

        private static void WriteTyped(OpenXmlWriter writer, string reference, CellValues type, string text)
        {
            writer.WriteStartElement(new Cell { CellReference = reference, DataType = type });
            writer.WriteElement(new CellValue(text));
            writer.WriteEndElement();
        }

        /// <summary>An <c>inlineStr</c> cell - never a shared string, and never a formula.</summary>
        private static void WriteInlineString(OpenXmlWriter writer, string reference, string text)
        {
            writer.WriteStartElement(new Cell { CellReference = reference, DataType = CellValues.InlineString });
            writer.WriteStartElement(new InlineString());
            writer.WriteElement(new Text(SanitiseXmlText(text)) { Space = SpaceProcessingModeValues.Preserve });
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        /// <summary>
        /// Drops the characters XML 1.0 cannot carry.
        /// </summary>
        /// <remarks>
        /// A control character such as <c>0x00</c>-<c>0x08</c> is legal in a database column and illegal in XML.
        /// Left in, it produces either a render that throws on one bad row or a .xlsx that Excel reports as
        /// corrupt long after the export was reported Ready. There is no escape sequence for these code points
        /// in XML 1.0, so dropping them is the only answer that keeps the rest of the cell. Tab, newline and
        /// carriage return are legal and kept; an unpaired surrogate is dropped, a paired one kept.
        /// </remarks>
        internal static string SanitiseXmlText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            StringBuilder? clean = null;

            for (var i = 0; i < text.Length; i++)
            {
                var character = text[i];

                if (char.IsHighSurrogate(character) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    clean?.Append(character).Append(text[i + 1]);
                    i++;
                    continue;
                }

                if (IsLegalXmlCharacter(character))
                {
                    clean?.Append(character);
                    continue;
                }

                clean ??= new StringBuilder(text.Length).Append(text, 0, i);
            }

            return clean?.ToString() ?? text;
        }

        private static bool IsLegalXmlCharacter(char character) =>
            character == '\t' || character == '\n' || character == '\r'
            || (character >= 0x20 && character <= 0xD7FF)
            || (character >= 0xE000 && character <= 0xFFFD);

        /// <summary>A sheet name Excel will accept, and that no other sheet in this workbook already uses.</summary>
        private static string UniqueSheetName(string? name, int ordinal, HashSet<string> used)
        {
            var candidate = SanitiseSheetName(name);
            if (candidate.Length == 0)
            {
                candidate = "Sheet" + ordinal.ToString(CultureInfo.InvariantCulture);
            }

            if (used.Add(candidate))
            {
                return candidate;
            }

            // Two tables may legitimately share a name; Excel will not open a workbook where two sheets do.
            for (var suffix = 2; ; suffix++)
            {
                var tail = "_" + suffix.ToString(CultureInfo.InvariantCulture);
                var trimmed = candidate.Length + tail.Length > MaximumSheetNameLength
                    ? candidate.Substring(0, MaximumSheetNameLength - tail.Length)
                    : candidate;

                if (used.Add(trimmed + tail))
                {
                    return trimmed + tail;
                }
            }
        }

        private static string SanitiseSheetName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(name!.Length);
            foreach (var character in name)
            {
                if (ForbiddenSheetNameCharacters.IndexOf(character) >= 0
                    || char.IsSurrogate(character)
                    || !IsLegalXmlCharacter(character))
                {
                    continue;
                }

                builder.Append(character);
            }

            // Excel also refuses a name that begins or ends with an apostrophe.
            var trimmed = builder.ToString().Trim().Trim('\'').Trim();
            return trimmed.Length > MaximumSheetNameLength ? trimmed.Substring(0, MaximumSheetNameLength).Trim() : trimmed;
        }

        private static void WriteWorkbook(Package package, List<(string Name, Uri Location)> sheets)
        {
            var workbookPart = package.CreatePart(WorkbookLocation, WorkbookContentType, CompressionOption.Normal);

            using (var stream = workbookPart.GetStream(FileMode.Create, FileAccess.Write))
            using (var writer = OpenXmlWriter.Create(stream))
            {
                writer.WriteStartElement(new Workbook());
                writer.WriteStartElement(new Sheets());

                for (var i = 0; i < sheets.Count; i++)
                {
                    var ordinal = (i + 1).ToString(CultureInfo.InvariantCulture);
                    writer.WriteElement(new Sheet
                    {
                        Name = sheets[i].Name,
                        SheetId = (uint)(i + 1),
                        Id = "rId" + ordinal,
                    });
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.Close();
            }

            // Relationships are held by the package and written when it closes - a few hundred bytes, however
            // many rows the sheets carried.
            for (var i = 0; i < sheets.Count; i++)
            {
                workbookPart.CreateRelationship(
                    sheets[i].Location, TargetMode.Internal, WorksheetRelationshipType,
                    "rId" + (i + 1).ToString(CultureInfo.InvariantCulture));
            }

            package.CreateRelationship(WorkbookLocation, TargetMode.Internal, OfficeDocumentRelationshipType, "rId1");
        }

        private static Uri SheetLocation(int ordinal) => new(
            "/xl/worksheets/sheet" + ordinal.ToString(CultureInfo.InvariantCulture) + ".xml", UriKind.Relative);

        /// <summary>What an empty document renders as its single sheet.</summary>
        private sealed class EmptyTable : ITabularTable
        {
            public static readonly EmptyTable Instance = new();

            public string Name => "Sheet1";

            public IReadOnlyList<ExportColumn> Columns { get; } = Array.Empty<ExportColumn>();

            public async IAsyncEnumerable<object?[]> ReadRowsAsync(
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
            {
                await Task.CompletedTask;
                yield break;
            }
        }

        private static string Reference(int columnIndex, uint rowIndex)
        {
            Span<char> letters = stackalloc char[3];
            var position = letters.Length;
            var remaining = columnIndex;

            do
            {
                letters[--position] = (char)('A' + (remaining % 26));
                remaining = (remaining / 26) - 1;
            }
            while (remaining >= 0);

            return string.Concat(letters.Slice(position), rowIndex.ToString(CultureInfo.InvariantCulture));
        }
    }
}
