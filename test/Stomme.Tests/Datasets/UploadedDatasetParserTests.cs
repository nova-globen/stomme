#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Stomme.Modules.Datasets.Application;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests.Datasets
{
    /// <summary>
    /// BL-94 increment 13 (D110 refutation 6) - the predecessor platform's UploadedDatasetParserTests (13) by name on the hardened parser,
    /// plus the port's additions: a numFmt date cell read as a date, duplicate headers rejected, a column past the
    /// sheet's width refused, rows beyond the sample counted without materialisation (a bounded allocation
    /// measurement), and the format decided by the ZIP signature plus the extension - never the content type, which the
    /// port no longer takes (the predecessor platform picked the parser from the client's content type first). CHANGED accordingly: the
    /// text overload is gone (everything is a stream), and the two content-type-driven cases assert the sniffing rule.
    /// </summary>
    public sealed class UploadedDatasetParserTests(ITestOutputHelper output)
    {
        private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        private static UploadedSample Parse(string text, string fileName) => UploadedDatasetParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(text)), fileName);

        private static UploadedSample Parse(byte[] bytes, string fileName) => UploadedDatasetParser.Parse(new MemoryStream(bytes), fileName);

        [Theory]
        [InlineData("x.json")]
        [InlineData("data.json")]
        public void Json_IsPassedThrough(string fileName)
        {
            const string json = "[ { \"amount\": \"10\" } ]";
            var sample = Parse(json, fileName);
            Assert.Equal((UploadFormat.Json, 1L), (sample.Format, sample.RowCount));
            Assert.Equal("10", (string?)JsonNode.Parse(sample.SampleJson)![0]!["amount"]);
        }

        [Fact]
        public void Csv_BecomesJsonRecordsKeyedByHeader()
        {
            var sample = Parse("age,name\n30,Ada\n40,Bo\n", "people.csv");
            var rows = Assert.IsType<JsonArray>(JsonNode.Parse(sample.SampleJson));
            Assert.Equal(2, rows.Count);
            Assert.Equal(("30", "Ada", "Bo"), ((string?)rows[0]!["age"], (string?)rows[0]!["name"], (string?)rows[1]!["name"]));
            Assert.Equal(2L, sample.RowCount);
        }

        [Fact]
        public void Csv_QuotedFields_MayContainCommasNewlinesAndEscapedQuotes()
        {
            var rows = Assert.IsType<JsonArray>(JsonNode.Parse(Parse("name,note\n\"Doe, John\",\"line1\nline2\"\n\"quote\"\"d\",plain\n", "x.csv").SampleJson));
            Assert.Equal(2, rows.Count);
            Assert.Equal(("Doe, John", "line1\nline2", "quote\"d", "plain"), ((string?)rows[0]!["name"], (string?)rows[0]!["note"], (string?)rows[1]!["name"], (string?)rows[1]!["note"]));
        }

        [Fact]
        public void Csv_HeaderOnly_YieldsAnEmptyArray()
        {
            var sample = Parse("a,b,c\n", "x.csv");
            Assert.Empty(Assert.IsType<JsonArray>(JsonNode.Parse(sample.SampleJson)));
            Assert.Equal(0L, sample.RowCount);
        }

        [Fact]
        public void Csv_ShortRow_FillsMissingTrailingColumnsWithNull()
        {
            var rows = Assert.IsType<JsonArray>(JsonNode.Parse(Parse("a,b\n1\n", "x.csv").SampleJson));
            Assert.Equal("1", (string?)rows[0]!["a"]);
            Assert.Null(rows[0]!["b"]);
        }

        [Theory]
        [InlineData("data.bin")]
        [InlineData("notes.txt")]
        public void UnsupportedFormat_IsRejected(string fileName) =>
            Assert.Throws<DatasetUploadRefusedException>(() => Parse("x", fileName));

        [Fact]
        public void Xlsx_ThroughTheStringOverload_IsRejected_DirectingToTheStreamOverload()
        {
            // CHANGED: there is no text overload; the rule it enforced - Excel is a package, not text - is now the signature's:
            // text named .xlsx is refused, and a package named anything else is refused.
            Assert.Throws<DatasetUploadRefusedException>(() => Parse("binary-bytes", "sheet.xlsx"));
            Assert.Throws<DatasetUploadRefusedException>(() => Parse(BuildXlsx(new[] { "a" }, new[] { new[] { "1" } }), "sheet.csv"));
        }

        [Fact]
        public void Xlsx_SharedStrings_BecomeJsonRecordsKeyedByHeader()
        {
            var sample = Parse(BuildXlsx(new[] { "age", "name" }, new[] { new[] { "30", "Ada" }, new[] { "40", "Bo" } }), "people.xlsx");
            var rows = Assert.IsType<JsonArray>(JsonNode.Parse(sample.SampleJson));
            Assert.Equal(2, rows.Count);
            Assert.Equal(("30", "Ada", "Bo"), ((string?)rows[0]!["age"], (string?)rows[0]!["name"], (string?)rows[1]!["name"]));
            Assert.Equal((UploadFormat.Xlsx, 2L), (sample.Format, sample.RowCount));
        }

        [Fact]
        public void Xlsx_DetectedByExtension_WhenContentTypeIsAbsent()
        {
            // CHANGED: the content type is never consulted; the signature plus the extension decide.
            var rows = Assert.IsType<JsonArray>(JsonNode.Parse(Parse(BuildXlsx(new[] { "a" }, new[] { new[] { "1" } }), "x.xlsx").SampleJson));
            Assert.Equal("1", (string?)rows[0]!["a"]);
            Assert.Equal(UploadFormat.Xlsx, UploadedDatasetParser.DetectFormat(new MemoryStream(BuildXlsx(new[] { "a" }, Array.Empty<string[]>())), "x.xlsx"));
        }

        [Fact]
        public void Xlsx_SparseRow_AlignsCellsByColumn_NotByPosition()
        {
            var rows = Assert.IsType<JsonArray>(JsonNode.Parse(Parse(BuildXlsxSparse(new[] { "a", "b", "c" }, new[] { (0, "x"), (2, "z") }), "sparse.xlsx").SampleJson));
            Assert.Equal("x", (string?)rows[0]!["a"]);
            Assert.Null(rows[0]!["b"]);
            Assert.Equal("z", (string?)rows[0]!["c"]);
        }

        [Fact]
        public void StreamOverload_Csv_IsStillParsed()
        {
            var rows = Assert.IsType<JsonArray>(JsonNode.Parse(Parse("a,b\n1,2\n", "x.csv").SampleJson));
            Assert.Equal(("1", "2"), ((string?)rows[0]!["a"], (string?)rows[0]!["b"]));
        }

        // ---- the port's additions --------------------------------------------------------------------------------------

        [Fact]
        public void A_numFmt_date_cell_is_read_as_a_date()
        {
            var bytes = BuildXlsxWithDate(new[] { "when", "n" }, 46_106, "7"); // 2026-03-25 as an Excel serial (day 0 is 1899-12-30, the OADate convention)
            var rows = Assert.IsType<JsonArray>(JsonNode.Parse(Parse(bytes, "dates.xlsx").SampleJson));
            Assert.Equal("2026-03-25T00:00:00", (string?)rows[0]!["when"]);
            Assert.Equal("7", (string?)rows[0]!["n"]); // a plain number stays a number's text
            Assert.Equal(Stomme.Datasets.ColumnDataType.Date, SchemaInference.Infer(Parse(bytes, "dates.xlsx").SampleJson).Schema.Columns[0].DataType);
        }

        [Fact]
        public void Duplicate_headers_are_rejected_in_csv_and_xlsx()
        {
            Assert.Contains("more than once", Assert.Throws<DatasetUploadRefusedException>(() => Parse("a,b,a\n1,2,3\n", "x.csv")).Message);
            Assert.Contains("more than once", Assert.Throws<DatasetUploadRefusedException>(() => Parse(BuildXlsx(new[] { "a", "a" }, new[] { new[] { "1", "2" } }), "x.xlsx")).Message);
            Assert.Throws<DatasetUploadRefusedException>(() => Parse(",,\n1,2\n", "x.csv")); // an all-empty header names nothing
        }

        [Fact]
        public void A_column_past_the_sheets_width_is_refused_and_a_blank_header_cell_is_named()
        {
            Assert.Equal((2, 16_383, 16_384, -1), (UploadedDatasetParser.ColumnIndex("C7"), UploadedDatasetParser.ColumnIndex("XFD1"), UploadedDatasetParser.ColumnIndex("XFE1"), UploadedDatasetParser.ColumnIndex(""))); // XFE is one past the sheet; the reader refuses it
            Assert.Contains("XFD", Assert.Throws<DatasetUploadRefusedException>(() => Parse(BuildXlsxSparse(new[] { "a" }, new[] { (16_384, "over") }), "wide.xlsx")).Message);
            var rows = Assert.IsType<JsonArray>(JsonNode.Parse(Parse("a,,c\n1,2,3\n", "x.csv").SampleJson));
            Assert.Equal("2", (string?)rows[0]!["column2"]);
        }

        [Fact]
        public void Rows_beyond_the_sample_are_counted_without_materialisation()
        {
            const int rowsInFile = 200_000;
            var builder = new StringBuilder("id,name,note\n");
            for (var i = 0; i < rowsInFile; i++)
            {
                builder.Append(i).Append(",name-").Append(i).Append(",\"a note, with a comma and some padding text to make the row wider\"\n");
            }

            var bytes = Encoding.UTF8.GetBytes(builder.ToString());
            output.WriteLine($"[csv] {bytes.Length / 1024 / 1024} MB, {rowsInFile} rows");

            // Warm the code path, then measure THIS thread's allocations over a full parse.
            UploadedDatasetParser.Parse(new MemoryStream(bytes, 0, 4096), "warm.csv");
            var before = GC.GetAllocatedBytesForCurrentThread();
            var sample = UploadedDatasetParser.Parse(new MemoryStream(bytes), "big.csv");
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            output.WriteLine($"[csv] allocated {allocated / 1024} KB parsing {bytes.Length / 1024} KB; sample {sample.SampleJson.Length} chars");

            Assert.Equal((rowsInFile, Stomme.Datasets.DatasetLimits.MaxSampleRows), (sample.RowCount, JsonNode.Parse(sample.SampleJson)!.AsArray().Count));
            // Past the sample the reader parses boundaries and quoting only - not one field is allocated - so the
            // allocation is a small fraction of the input (measured: the first draft decoded every field to a string
            // and allocated 3.8x the input; now well under it).
            Assert.True(allocated < bytes.Length, $"parsing {bytes.Length} bytes allocated {allocated} bytes - the rows past the sample were materialised");
        }

        [Fact]
        public void Json_is_read_through_a_byte_cap_and_a_depth_cap_before_it_is_parsed()
        {
            var big = "[" + string.Join(",", Enumerable.Range(0, 60_000).Select(i => "{\"n\":\"" + new string('x', 30) + "\"}")) + "]";
            Assert.True(big.Length > Stomme.Datasets.DatasetLimits.MaxSampleBytes);
            Assert.Contains("bytes", Assert.Throws<DatasetUploadRefusedException>(() => Parse(big, "big.json")).Message);

            var deep = new string('[', 100) + new string(']', 100);
            Assert.Contains("deeper", Assert.Throws<DatasetUploadRefusedException>(() => Parse(deep, "deep.json")).Message);
            Assert.Throws<DatasetUploadRefusedException>(() => Parse("{ \"not\": \"an array\" }", "x.json"));
            Assert.Throws<DatasetUploadRefusedException>(() => Parse("[1, 2]", "x.json"));
            Assert.Throws<DatasetUploadRefusedException>(() => Parse("not json", "x.json"));

            var sample = Parse("[" + string.Join(",", Enumerable.Range(0, 120).Select(i => "{\"n\":\"" + i + "\"}")) + "]", "many.json");
            Assert.Equal((120L, Stomme.Datasets.DatasetLimits.MaxSampleRows), (sample.RowCount, JsonNode.Parse(sample.SampleJson)!.AsArray().Count));
        }

        // ---- xlsx builders (shared-string workbooks, as Excel writes them) ---------------------------------------------

        internal static byte[] BuildXlsx(string[] headers, string[][] dataRows)
        {
            var rows = new List<(uint Row, (int Col, string Value)[] Cells)> { (1, headers.Select((h, i) => (i, h)).ToArray()) };
            for (var r = 0; r < dataRows.Length; r++)
            {
                rows.Add(((uint)(r + 2), dataRows[r].Select((v, i) => (i, v)).ToArray()));
            }

            return WriteWorkbook(rows);
        }

        internal static byte[] BuildXlsxSparse(string[] headers, (int Col, string Value)[] singleDataRow) =>
            WriteWorkbook(new List<(uint, (int, string)[])> { (1, headers.Select((h, i) => (i, h)).ToArray()), (2, singleDataRow) });

        /// <summary>A workbook whose first data cell is a NUMBER under a built-in date format (14), the second a plain number.</summary>
        internal static byte[] BuildXlsxWithDate(string[] headers, double serial, string number)
        {
            using var ms = new MemoryStream();
            using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
            {
                var workbookPart = doc.AddWorkbookPart();
                workbookPart.Workbook = new Workbook();
                var styles = workbookPart.AddNewPart<WorkbookStylesPart>();
                styles.Stylesheet = new Stylesheet(
                    new Fonts(new Font()),
                    new Fills(new Fill()),
                    new Borders(new Border()),
                    new CellFormats(new CellFormat(), new CellFormat { NumberFormatId = 14, ApplyNumberFormat = true }));
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();
                worksheetPart.Worksheet = new Worksheet(sheetData);
                var header = new Row { RowIndex = 1 };
                for (var i = 0; i < headers.Length; i++)
                {
                    header.Append(new Cell { CellReference = ColumnLetter(i) + "1", DataType = CellValues.InlineString, InlineString = new InlineString(new Text(headers[i])) });
                }

                sheetData.Append(header);
                var row = new Row { RowIndex = 2 };
                row.Append(new Cell { CellReference = "A2", StyleIndex = 1, CellValue = new CellValue(serial.ToString(System.Globalization.CultureInfo.InvariantCulture)) });
                row.Append(new Cell { CellReference = "B2", CellValue = new CellValue(number) });
                sheetData.Append(row);
                workbookPart.Workbook.AppendChild(new Sheets()).Append(new Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "Sheet1" });
            }

            return ms.ToArray();
        }

        private static byte[] WriteWorkbook(List<(uint Row, (int Col, string Value)[] Cells)> rows)
        {
            using var ms = new MemoryStream();
            using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
            {
                var workbookPart = doc.AddWorkbookPart();
                workbookPart.Workbook = new Workbook();
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                var sheetData = new SheetData();
                worksheetPart.Worksheet = new Worksheet(sheetData);
                var sstPart = workbookPart.AddNewPart<SharedStringTablePart>();
                sstPart.SharedStringTable = new SharedStringTable();
                var sharedIndex = new Dictionary<string, int>(StringComparer.Ordinal);
                int Intern(string value)
                {
                    if (!sharedIndex.TryGetValue(value, out var i))
                    {
                        i = sharedIndex.Count;
                        sharedIndex[value] = i;
                        sstPart.SharedStringTable.Append(new SharedStringItem(new Text(value)));
                    }

                    return i;
                }

                foreach (var (rowIndex, cells) in rows)
                {
                    var row = new Row { RowIndex = rowIndex };
                    foreach (var (col, value) in cells)
                    {
                        row.Append(new Cell { CellReference = $"{ColumnLetter(col)}{rowIndex}", DataType = CellValues.SharedString, CellValue = new CellValue(Intern(value).ToString()) });
                    }

                    sheetData.Append(row);
                }

                workbookPart.Workbook.AppendChild(new Sheets()).Append(new Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "Sheet1" });
            }

            return ms.ToArray();
        }

        internal static string ColumnLetter(int zeroBasedColumn)
        {
            var n = zeroBasedColumn + 1;
            var letters = string.Empty;
            while (n > 0)
            {
                var remainder = (n - 1) % 26;
                letters = (char)('A' + remainder) + letters;
                n = (n - 1) / 26;
            }

            return letters;
        }
    }
}
