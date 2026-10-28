#nullable enable
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Export;

namespace Stomme.Modules.DataExport.Rendering
{
    /// <summary>
    /// JSON: an object per table, an object per row, written through a streaming writer (BL-85).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No formula neutralisation, deliberately.</b> JSON is not opened by a spreadsheet, and a leading
    /// apostrophe here would corrupt the value for every legitimate consumer — the guard belongs where the
    /// hazard is. The value is written as a JSON string or as its typed literal; nothing is executed by reading
    /// it back.
    /// </para>
    /// <para>
    /// <b>Duplicate column names are de-duplicated</b>, because a JSON object cannot carry the same property
    /// twice and a source is free to declare two columns called "Name". The second becomes "Name_2", which is
    /// visible and lossless, rather than silently overwriting the first.
    /// </para>
    /// </remarks>
    internal sealed class JsonTabularRenderer : ITabularRenderer
    {
        public ExportFormat Format => ExportFormat.Json;

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

            await using var writer = new Utf8JsonWriter(destination, new JsonWriterOptions
            {
                Indented = options.JsonIndented,
                SkipValidation = false,
            });

            var rows = 0L;
            writer.WriteStartObject();
            writer.WriteStartArray("tables");

            await foreach (var table in document.ReadTablesAsync(ct).WithCancellation(ct))
            {
                writer.WriteStartObject();
                writer.WriteString("name", table.Name);
                writer.WriteStartArray("rows");

                var names = PropertyNames(table);

                await foreach (var row in table.ReadRowsAsync(ct).WithCancellation(ct))
                {
                    ct.ThrowIfCancellationRequested();

                    writer.WriteStartObject();
                    for (var i = 0; i < table.Columns.Count; i++)
                    {
                        var value = i < row.Length ? row[i] : null;
                        WriteValue(writer, names[i], value, table.Columns[i], formatter);
                    }

                    writer.WriteEndObject();
                    rows++;

                    // Keep the writer's own buffer small rather than letting it grow across a million rows.
                    if (writer.BytesPending > 32 * 1024)
                    {
                        await writer.FlushAsync(ct);
                    }
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            await writer.FlushAsync(ct);

            return new RenderOutcome(rows);
        }

        /// <summary>Property names, made unique in declaration order.</summary>
        private static string[] PropertyNames(ITabularTable table)
        {
            var names = new string[table.Columns.Count];
            var seen = new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);

            for (var i = 0; i < table.Columns.Count; i++)
            {
                var name = table.Columns[i].Name;
                if (seen.TryGetValue(name, out var count))
                {
                    seen[name] = count + 1;
                    names[i] = $"{name}_{count + 1}";
                }
                else
                {
                    seen[name] = 1;
                    names[i] = name;
                }
            }

            return names;
        }

        /// <summary>
        /// Writes the value with its declared type when the runtime type agrees, and as a STRING when it does not.
        /// </summary>
        /// <remarks>
        /// The mismatch rule again: a column promising Integer that hands over "oops" must not produce a broken
        /// JSON number. A string is lossless and obviously wrong to a reader.
        /// </remarks>
        private static void WriteValue(
            Utf8JsonWriter writer, string name, object? value, ExportColumn column, IExportCellFormatter formatter)
        {
            if (value is null)
            {
                writer.WriteNull(name);
                return;
            }

            var formatted = formatter.Format(value, column);
            if (formatted != null)
            {
                writer.WriteString(name, formatted);
                return;
            }

            if (!ExportCellText.Matches(value, column.Kind))
            {
                writer.WriteString(name, ExportCellText.Format(value, column, formatter));
                return;
            }

            switch (column.Kind)
            {
                case ExportValueKind.Integer:
                    writer.WriteNumber(name, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case ExportValueKind.Decimal:
                    writer.WriteNumber(name, Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case ExportValueKind.Boolean:
                    writer.WriteBoolean(name, (bool)value);
                    break;
                default:
                    writer.WriteString(name, ExportCellText.Format(value, column, formatter));
                    break;
            }
        }
    }
}
