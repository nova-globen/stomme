#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Export;

namespace Stomme.Modules.DataExport.Rendering
{
    /// <summary>
    /// CSV: one table, streamed, with every cell neutralised (BL-85).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One table, checked twice.</b> Request validation refuses CSV for a source that DECLARES
    /// <see cref="TableCardinality.Multiple"/>, and this renderer fails the render if a second table appears
    /// anyway. The declaration is a promise made before anything is opened — a source can break it, by a bug or
    /// otherwise — and silently writing the second table's rows underneath the first would produce a file whose
    /// columns change halfway down and which no consumer can read back.
    /// </para>
    /// <para>
    /// <b>Nothing is buffered.</b> Rows are pulled one at a time and written straight through; the caller counts
    /// the bytes on the destination to enforce the artifact cap. A renderer that built the file in a
    /// <c>MemoryStream</c> would make the cap fire only after the whole thing was already in memory.
    /// </para>
    /// </remarks>
    internal sealed class CsvTabularRenderer : ITabularRenderer
    {
        public ExportFormat Format => ExportFormat.Csv;

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

            if (!ExportRenderOptions.IsAllowedCsvDelimiter(options.CsvDelimiter))
            {
                throw new ExportRenderException(
                    $"'{options.CsvDelimiter}' is not an accepted CSV delimiter. Allowed: comma, semicolon, tab, pipe.");
            }

            // No BOM: a UTF-8 BOM is itself a leading byte sequence that some parsers surface as part of the
            // first header cell.
            await using var writer = new StreamWriter(destination, new UTF8Encoding(false), 64 * 1024, leaveOpen: true)
            {
                NewLine = "\r\n",
            };

            var rows = 0L;
            var tables = 0;

            await foreach (var table in document.ReadTablesAsync(ct).WithCancellation(ct))
            {
                if (++tables > 1)
                {
                    throw new ExportRenderException(
                        "CSV holds a single table, and this export produced a second one. The source declared " +
                        "Single cardinality; that declaration is wrong.");
                }

                if (options.IncludeHeader)
                {
                    for (var i = 0; i < table.Columns.Count; i++)
                    {
                        if (i > 0)
                        {
                            await writer.WriteAsync(options.CsvDelimiter);
                        }

                        // The header is data too: a column NAME can begin with '=' just as a value can.
                        var name = ExportCellText.Neutralise(table.Columns[i].Name, options.NeutraliseFormulas);
                        await writer.WriteAsync(ExportCellText.CsvField(name, options.CsvDelimiter));
                    }

                    await writer.WriteLineAsync();
                }

                await foreach (var row in table.ReadRowsAsync(ct).WithCancellation(ct))
                {
                    ct.ThrowIfCancellationRequested();

                    for (var i = 0; i < table.Columns.Count; i++)
                    {
                        if (i > 0)
                        {
                            await writer.WriteAsync(options.CsvDelimiter);
                        }

                        var value = i < row.Length ? row[i] : null;
                        var text = ExportCellText.SafeText(value, table.Columns[i], formatter, options.NeutraliseFormulas);
                        await writer.WriteAsync(ExportCellText.CsvField(text, options.CsvDelimiter));
                    }

                    await writer.WriteLineAsync();
                    rows++;
                }
            }

            await writer.FlushAsync();
            return new RenderOutcome(rows);
        }
    }

    /// <summary>A render that cannot continue — reported as the job's failure reason, never as a crash.</summary>
    public sealed class ExportRenderException : Exception
    {
        public ExportRenderException(string message) : base(message)
        {
        }
    }
}
