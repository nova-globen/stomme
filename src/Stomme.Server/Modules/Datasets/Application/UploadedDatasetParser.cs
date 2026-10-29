#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stomme.Datasets;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Stomme.Modules.Datasets.Application
{
    /// <summary>The formats an upload may take; decided by content sniffing (the ZIP signature) plus the extension, never the content type.</summary>
    public enum UploadFormat
    {
        Json = 0,
        Csv = 1,
        Xlsx = 2,
    }

    /// <summary>What a parse produced: the bounded sample as a JSON array, the FULL row count (rows past the sample are counted, not kept), and the format.</summary>
    public sealed record UploadedSample(string SampleJson, long RowCount, UploadFormat Format);

    /// <summary>An upload the parser refuses - the reason is the caller's, never a parser's internals.</summary>
    public sealed class DatasetUploadRefusedException(string message) : Exception(message);

    /// <summary>
    /// Converts an uploaded file into the sample rows dataset inference consumes (hardened against hostile files):
    /// the format is decided by the ZIP signature plus the extension (an XLSX renamed
    /// <c>.csv</c> is refused, whatever it was served as); JSON is read through a byte cap before parsing, with a depth
    /// cap; CSV is streamed line by line, materialising only the sample while counting every row; XLSX is checked as a
    /// ZIP first (a part count, a declared-size and a compression-ratio cap), then read through OpenXml's SAX reader with
    /// <c>MaxCharactersInPart</c>, the shared strings indexed once, cells aligned by column, duplicate headers and a
    /// column past the sheet's width refused, and a date-formatted numeric cell read as a date. Nothing here reads a
    /// whole file into memory (<c>UploadNoWholeBufferTests</c>).
    /// </summary>
    public static class UploadedDatasetParser
    {
        public const int MaxJsonDepth = 32;
        public const int MaxXlsxParts = 64;
        public const long MaxXlsxDeclaredPartBytes = 64L * 1024 * 1024;
        public const long MaxXlsxDeclaredTotalBytes = 128L * 1024 * 1024;
        public const double MaxXlsxCompressionRatio = 100;
        public const long XlsxRatioFloorBytes = 64L * 1024;
        public const long MaxXlsxCharactersInPart = 16L * 1024 * 1024;
        public const int MaxSheetColumns = 16_384; // XFD, Excel's own width
        private const int MaxCsvFieldChars = 1024 * 1024;

        private static readonly byte[] ZipSignature = { 0x50, 0x4B, 0x03, 0x04 };
        private static readonly HashSet<uint> BuiltInDateFormats = new() { 14, 15, 16, 17, 18, 19, 20, 21, 22, 45, 46, 47 };

        /// <summary>The format, from the first bytes of <paramref name="content"/> (it must be seekable; the position is restored) and the file's extension.</summary>
        public static UploadFormat DetectFormat(Stream content, string? fileName)
        {
            ArgumentNullException.ThrowIfNull(content);
            var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
            var zip = StartsWithZipSignature(content);
            return (zip, extension) switch
            {
                (true, ".xlsx") => UploadFormat.Xlsx,
                (true, _) => throw new DatasetUploadRefusedException($"'{fileName}' is a ZIP package but is not named .xlsx; a spreadsheet is uploaded under its own extension."),
                (false, ".xlsx") => throw new DatasetUploadRefusedException($"'{fileName}' is named .xlsx but is not a spreadsheet package."),
                (false, ".csv") => UploadFormat.Csv,
                (false, ".json") => UploadFormat.Json,
                _ => throw new DatasetUploadRefusedException($"'{fileName}' is not a supported dataset upload; supported: .json, .csv, .xlsx."),
            };
        }

        /// <summary>Parses <paramref name="content"/> (seekable) into the bounded sample and the full row count.</summary>
        public static UploadedSample Parse(Stream content, string? fileName, int maxSampleRows = DatasetLimits.MaxSampleRows, int maxSampleBytes = DatasetLimits.MaxSampleBytes)
        {
            var format = DetectFormat(content, fileName);
            return format switch
            {
                UploadFormat.Json => ParseJson(content, maxSampleRows, maxSampleBytes),
                UploadFormat.Csv => ParseCsv(content, maxSampleRows),
                _ => ParseXlsx(content, maxSampleRows),
            };
        }

        // ---- JSON: a byte cap BEFORE the parse, a depth cap IN it ---------------------------------------------------

        private static UploadedSample ParseJson(Stream content, int maxSampleRows, int maxSampleBytes)
        {
            var buffer = new byte[Math.Min(maxSampleBytes + 1, int.MaxValue - 1)];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = content.Read(buffer, read, buffer.Length - read);
                if (n <= 0)
                {
                    break;
                }

                read += n;
            }

            if (read > maxSampleBytes)
            {
                throw new DatasetUploadRefusedException($"A JSON dataset upload is read up to {maxSampleBytes} bytes; this one is larger. Upload a sample, or a CSV.");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, 0, read), new JsonDocumentOptions { MaxDepth = MaxJsonDepth, AllowTrailingCommas = true });
            }
            catch (JsonException ex)
            {
                throw new DatasetUploadRefusedException("The JSON upload is malformed or nested deeper than " + MaxJsonDepth + " levels: " + ex.GetType().Name + ".");
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Array)
                {
                    throw new DatasetUploadRefusedException("A JSON dataset upload is an array of objects.");
                }

                var sample = new JsonArray();
                long count = 0;
                foreach (var element in root.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        throw new DatasetUploadRefusedException("A JSON dataset upload is an array of objects; an element is not an object.");
                    }

                    count++;
                    if (sample.Count < maxSampleRows)
                    {
                        sample.Add(JsonNode.Parse(element.GetRawText()));
                    }
                }

                return new UploadedSample(sample.ToJsonString(), count, UploadFormat.Json);
            }
        }

        // ---- CSV: streamed, RFC 4180, the sample materialised and the rest counted -------------------------------------

        private static UploadedSample ParseCsv(Stream content, int maxSampleRows)
        {
            using var reader = new StreamReader(content, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: true);
            var sample = new JsonArray();
            var records = CsvRecords(reader, () => sample.Count < maxSampleRows).GetEnumerator();
            if (!records.MoveNext())
            {
                return new UploadedSample("[]", 0, UploadFormat.Csv);
            }

            var header = RequireHeader(records.Current.Fields ?? new List<string>());
            long count = 0;
            while (records.MoveNext())
            {
                var record = records.Current;
                if (record.Blank)
                {
                    continue; // a blank line
                }

                count++;
                if (record.Fields is not { } fields)
                {
                    continue; // past the sample: counted, its boundaries and quoting parsed, not one field of it allocated
                }

                var row = new JsonObject();
                for (var c = 0; c < header.Count; c++)
                {
                    row[header[c]] = c < fields.Count ? JsonValue.Create(fields[c]) : null;
                }

                sample.Add(row);
            }

            return new UploadedSample(sample.ToJsonString(), count, UploadFormat.Csv);
        }

        /// <summary>One CSV record: its fields when the caller still wants them, else only whether the line was blank.</summary>
        private readonly record struct CsvRecord(List<string>? Fields, bool Blank);

        /// <summary>
        /// RFC 4180 records, streamed. While <paramref name="materialise"/> answers true the fields are built; once it
        /// answers false only the record boundaries and the quoting are parsed and nothing per field is allocated -
        /// which is what lets a file of any row count be COUNTED within a bounded allocation. The field-length cap
        /// applies in both modes.
        /// </summary>
        private static IEnumerable<CsvRecord> CsvRecords(TextReader reader, Func<bool> materialise)
        {
            var field = new StringBuilder();
            var record = new List<string>();
            var inQuotes = false;
            var any = false;
            var keep = materialise();
            var fieldLength = 0;
            var rowHasContent = false;
            int next;
            while ((next = reader.Read()) >= 0)
            {
                var ch = (char)next;
                any = true;
                if (inQuotes)
                {
                    if (ch == '"')
                    {
                        if (reader.Peek() == '"')
                        {
                            reader.Read();
                            Append('"');
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        Append(ch);
                    }

                    continue;
                }

                switch (ch)
                {
                    case '"':
                        inQuotes = true;
                        rowHasContent = true;
                        break;
                    case ',':
                        EndField();
                        rowHasContent = true;
                        break;
                    case '\r':
                        break;
                    case '\n':
                        EndField();
                        yield return keep ? new CsvRecord(record, record.Count == 1 && record[0].Length == 0) : new CsvRecord(null, !rowHasContent);
                        record = keep ? new List<string>() : record;
                        any = false;
                        rowHasContent = false;
                        keep = materialise();
                        break;
                    default:
                        Append(ch);
                        break;
                }
            }

            if (any || fieldLength > 0 || record.Count > 0)
            {
                EndField();
                yield return keep ? new CsvRecord(record, record.Count == 1 && record[0].Length == 0) : new CsvRecord(null, !rowHasContent);
            }

            void Append(char c)
            {
                rowHasContent = true;
                fieldLength++;
                if (fieldLength > MaxCsvFieldChars)
                {
                    throw new DatasetUploadRefusedException($"A CSV field is longer than {MaxCsvFieldChars} characters.");
                }

                if (keep)
                {
                    field.Append(c);
                }
            }

            void EndField()
            {
                if (keep)
                {
                    record.Add(field.ToString());
                    field.Clear();
                }

                fieldLength = 0;
            }
        }

        private static List<string> RequireHeader(IReadOnlyList<string> names)
        {
            var header = names.Select(name => name.Trim()).ToList();
            if (header.Count == 0 || header.All(string.IsNullOrEmpty))
            {
                throw new DatasetUploadRefusedException("The first row names the columns; it is empty.");
            }

            var duplicate = header.Where(name => name.Length > 0).GroupBy(name => name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                throw new DatasetUploadRefusedException($"The header names column '{duplicate.Key}' more than once.");
            }

            for (var i = 0; i < header.Count; i++)
            {
                if (header[i].Length == 0)
                {
                    header[i] = "column" + (i + 1).ToString(CultureInfo.InvariantCulture);
                }
            }

            return header;
        }

        // ---- XLSX: the ZIP checked first, then SAX with a character cap ---------------------------------------------

        private static UploadedSample ParseXlsx(Stream content, int maxSampleRows)
        {
            CheckPackage(content);
            content.Position = 0;
            try
            {
                using var document = SpreadsheetDocument.Open(content, false, new OpenSettings { MaxCharactersInPart = MaxXlsxCharactersInPart });
                var workbook = document.WorkbookPart ?? throw new DatasetUploadRefusedException("The spreadsheet has no workbook.");
                var sheet = workbook.Workbook?.Sheets?.Elements<Sheet>().FirstOrDefault();
                if (sheet?.Id?.Value is null)
                {
                    return new UploadedSample("[]", 0, UploadFormat.Xlsx);
                }

                var sharedStrings = ReadSharedStrings(workbook.SharedStringTablePart);
                var dateStyles = DateStyleIndexes(workbook.WorkbookStylesPart);
                var worksheet = (WorksheetPart)workbook.GetPartById(sheet.Id.Value!);

                List<string>? header = null;
                var headerColumns = new List<int>();
                var sample = new JsonArray();
                long count = 0;
                using var reader = OpenXmlReader.Create(worksheet);
                while (reader.Read())
                {
                    if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
                    {
                        continue;
                    }

                    var cells = ReadRow(reader, sharedStrings, dateStyles);
                    if (header is null)
                    {
                        if (cells.Count == 0)
                        {
                            continue;
                        }

                        var ordered = cells.OrderBy(cell => cell.Key).ToList();
                        headerColumns = ordered.Select(cell => cell.Key).ToList();
                        header = RequireHeader(ordered.Select(cell => cell.Value).ToList());
                        continue;
                    }

                    if (cells.Count == 0)
                    {
                        continue;
                    }

                    count++;
                    if (sample.Count >= maxSampleRows)
                    {
                        continue;
                    }

                    var row = new JsonObject();
                    for (var i = 0; i < header.Count; i++)
                    {
                        row[header[i]] = cells.TryGetValue(headerColumns[i], out var value) ? JsonValue.Create(value) : null;
                    }

                    sample.Add(row);
                }

                return new UploadedSample(sample.ToJsonString(), count, UploadFormat.Xlsx);
            }
            catch (OpenXmlPackageException ex)
            {
                throw new DatasetUploadRefusedException("The spreadsheet could not be read within the parser's limits: " + ex.GetType().Name + ".");
            }
            catch (InvalidDataException ex)
            {
                throw new DatasetUploadRefusedException("The spreadsheet package is not readable: " + ex.GetType().Name + ".");
            }
        }

        /// <summary>The package as a ZIP, before OpenXml touches it: the part count, each part's declared size, the total, and the compression ratio.</summary>
        private static void CheckPackage(Stream content)
        {
            content.Position = 0;
            using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > MaxXlsxParts)
            {
                throw new DatasetUploadRefusedException($"The spreadsheet package has {archive.Entries.Count} parts; at most {MaxXlsxParts} are read.");
            }

            long total = 0;
            foreach (var entry in archive.Entries)
            {
                if (entry.Length > MaxXlsxDeclaredPartBytes)
                {
                    throw new DatasetUploadRefusedException($"Part '{entry.FullName}' declares {entry.Length} bytes; at most {MaxXlsxDeclaredPartBytes} are read.");
                }

                total += entry.Length;
                if (total > MaxXlsxDeclaredTotalBytes)
                {
                    throw new DatasetUploadRefusedException($"The spreadsheet package declares more than {MaxXlsxDeclaredTotalBytes} bytes in total.");
                }

                if (entry.Length > XlsxRatioFloorBytes && entry.CompressedLength > 0 && (double)entry.Length / entry.CompressedLength > MaxXlsxCompressionRatio)
                {
                    throw new DatasetUploadRefusedException($"Part '{entry.FullName}' expands more than {MaxXlsxCompressionRatio:0}:1.");
                }
            }
        }

        private static List<string> ReadSharedStrings(SharedStringTablePart? part)
        {
            var strings = new List<string>();
            if (part is null)
            {
                return strings;
            }

            using var reader = OpenXmlReader.Create(part);
            while (reader.Read())
            {
                if (reader.ElementType == typeof(SharedStringItem) && reader.IsStartElement)
                {
                    var item = (SharedStringItem)reader.LoadCurrentElement()!;
                    strings.Add(item.InnerText);
                }
            }

            return strings;
        }

        private static HashSet<uint> DateStyleIndexes(WorkbookStylesPart? styles)
        {
            var result = new HashSet<uint>();
            var stylesheet = styles?.Stylesheet;
            if (stylesheet?.CellFormats is null)
            {
                return result;
            }

            var customDateFormats = new HashSet<uint>();
            foreach (var format in stylesheet.NumberingFormats?.Elements<NumberingFormat>() ?? Enumerable.Empty<NumberingFormat>())
            {
                var code = format.FormatCode?.Value ?? string.Empty;
                var stripped = code.Replace("\"", "").Replace("[", "").Replace("]", "");
                if (format.NumberFormatId is not null && (stripped.Contains('y') || stripped.Contains('d') || (stripped.Contains('m') && !stripped.Contains('0'))))
                {
                    customDateFormats.Add(format.NumberFormatId.Value);
                }
            }

            uint index = 0;
            foreach (var cellFormat in stylesheet.CellFormats.Elements<CellFormat>())
            {
                var id = cellFormat.NumberFormatId?.Value ?? 0;
                if (BuiltInDateFormats.Contains(id) || customDateFormats.Contains(id))
                {
                    result.Add(index);
                }

                index++;
            }

            return result;
        }

        private static Dictionary<int, string> ReadRow(OpenXmlReader reader, List<string> sharedStrings, HashSet<uint> dateStyles)
        {
            var cells = new Dictionary<int, string>();
            var row = (Row)reader.LoadCurrentElement()!;
            foreach (var cell in row.Elements<Cell>())
            {
                var column = ColumnIndex(cell.CellReference?.Value);
                if (column < 0)
                {
                    continue;
                }

                if (column >= MaxSheetColumns)
                {
                    throw new DatasetUploadRefusedException($"Cell '{cell.CellReference?.Value}' lies past column XFD, the widest a sheet can be.");
                }

                cells[column] = CellText(cell, sharedStrings, dateStyles);
            }

            return cells;
        }

        private static string CellText(Cell cell, List<string> sharedStrings, HashSet<uint> dateStyles)
        {
            var raw = cell.CellValue?.InnerText ?? cell.InnerText ?? string.Empty;
            if (cell.DataType?.Value == CellValues.SharedString)
            {
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < sharedStrings.Count ? sharedStrings[index] : string.Empty;
            }

            if (cell.DataType?.Value == CellValues.Boolean)
            {
                return raw == "1" ? "TRUE" : "FALSE";
            }

            if (cell.DataType?.Value == CellValues.InlineString)
            {
                return cell.InlineString?.InnerText ?? raw;
            }

            if ((cell.DataType is null || cell.DataType.Value == CellValues.Number) && cell.StyleIndex is not null && dateStyles.Contains(cell.StyleIndex.Value)
                && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial >= 0 && serial < 2_958_466)
            {
                // A numFmt date: the serial as an ISO instant, so inference types the column Date.
                return DateTime.FromOADate(serial).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            }

            return raw;
        }

        /// <summary>Zero-based column index from the letter prefix of a cell reference (<c>C7</c> → 2); -1 when absent.</summary>
        public static int ColumnIndex(string? cellReference)
        {
            if (string.IsNullOrEmpty(cellReference))
            {
                return -1;
            }

            long column = 0;
            var letters = 0;
            foreach (var ch in cellReference)
            {
                if (ch is >= 'A' and <= 'Z')
                {
                    column = column * 26 + (ch - 'A' + 1);
                }
                else if (ch is >= 'a' and <= 'z')
                {
                    column = column * 26 + (ch - 'a' + 1);
                }
                else
                {
                    break;
                }

                if (++letters > 3)
                {
                    return int.MaxValue; // four letters is already past XFD; refused by the caller
                }
            }

            return column == 0 ? -1 : (int)(column - 1);
        }

        private static bool StartsWithZipSignature(Stream content)
        {
            var position = content.Position;
            content.Position = 0;
            Span<byte> head = stackalloc byte[4];
            var read = content.Read(head);
            content.Position = position;
            return read == 4 && head.SequenceEqual(ZipSignature);
        }
    }
}
