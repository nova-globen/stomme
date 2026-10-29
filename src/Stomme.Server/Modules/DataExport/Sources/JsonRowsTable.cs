#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Datasets;
using Stomme.Export;

namespace Stomme.Modules.DataExport.Sources
{
    /// <summary>
    /// One export table over a module's BOUNDED JSON rows (the predecessor platform's <c>ExportSourceRows</c>): a JSON
    /// array of objects - the shape the Datasets catalogue's preview and a dashboard widget's data carry - projected onto a
    /// fixed, ordered column list, a cell absent from a row becoming null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bounded by the source, not by this type.</b> The rows are a dataset's stored sample (at most
    /// <see cref="DatasetLimits.MaxSampleRows"/>), so parsing them once is the whole cost; a source over an unbounded
    /// store must page instead, as the event-log source does.
    /// </para>
    /// <para>
    /// <b>Values keep their JSON type</b> (a number is a <c>long</c> or a <c>decimal</c>, <c>true</c> a <c>bool</c>), and a
    /// JSON STRING is converted toward its column's declared kind only when it parses exactly (invariant culture) - a
    /// value that does not parse stays the string it was, which every renderer treats as text rather than a mis-typed
    /// cell. A nested object or array is exported as its JSON text.
    /// </para>
    /// </remarks>
    public sealed class JsonRowsTable : ITabularTable
    {
        private readonly string _rowsJson;

        public JsonRowsTable(string name, IReadOnlyList<ExportColumn> columns, string rowsJson)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Columns = columns ?? throw new ArgumentNullException(nameof(columns));
            _rowsJson = rowsJson ?? string.Empty;
        }

        public string Name { get; }

        public IReadOnlyList<ExportColumn> Columns { get; }

        /// <summary>The export kind a dataset column's data type declares; free text and JSON are text.</summary>
        public static ExportValueKind KindOf(ColumnDataType type) => type switch
        {
            ColumnDataType.Number => ExportValueKind.Decimal,
            ColumnDataType.Boolean => ExportValueKind.Boolean,
            ColumnDataType.Date => ExportValueKind.DateTimeUtc,
            _ => ExportValueKind.Text,
        };

        public async IAsyncEnumerable<object?[]> ReadRowsAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            if (string.IsNullOrWhiteSpace(_rowsJson))
            {
                yield break;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(_rowsJson);
            }
            catch (JsonException)
            {
                yield break; // the source's rows are its own stored JSON; unreadable rows export as none, never as a crash
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    yield return Project(root);
                    yield break;
                }

                if (root.ValueKind != JsonValueKind.Array)
                {
                    yield break;
                }

                foreach (var item in root.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        yield return Project(item);
                    }
                }
            }
        }

        private object?[] Project(JsonElement row)
        {
            var values = new object?[Columns.Count];
            for (var i = 0; i < Columns.Count; i++)
            {
                values[i] = TryGetProperty(row, Columns[i].Name, out var cell) ? ReadValue(cell, Columns[i].Kind) : null;
            }

            return values;
        }

        // An exact name first; otherwise the first property equal ignoring case, as the schema check compares names.
        private static bool TryGetProperty(JsonElement row, string name, out JsonElement value)
        {
            if (row.TryGetProperty(name, out value))
            {
                return true;
            }

            foreach (var property in row.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        internal static object? ReadValue(JsonElement value, ExportValueKind kind)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Number:
                    if (value.TryGetInt64(out var integer))
                    {
                        return integer;
                    }

                    return value.TryGetDecimal(out var number) ? number : value.GetDouble();
                case JsonValueKind.String:
                    return Convert(value.GetString() ?? string.Empty, kind);
                default:
                    return value.GetRawText();
            }
        }

        private static object Convert(string text, ExportValueKind kind)
        {
            switch (kind)
            {
                case ExportValueKind.Decimal when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var number):
                    return number;
                case ExportValueKind.Integer when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer):
                    return integer;
                case ExportValueKind.Boolean when bool.TryParse(text, out var flag):
                    return flag;
                case ExportValueKind.DateTimeUtc when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var instant):
                    return DateTime.SpecifyKind(instant, DateTimeKind.Utc);
                case ExportValueKind.Guid when Guid.TryParse(text, out var id):
                    return id;
                default:
                    return text;
            }
        }
    }

    /// <summary>A document over tables a source already built; nothing to release.</summary>
    public sealed class ExportTablesDocument : ITabularDocument
    {
        private readonly IReadOnlyList<ITabularTable> _tables;

        public ExportTablesDocument(IReadOnlyList<ITabularTable> tables)
        {
            _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        }

        public async IAsyncEnumerable<ITabularTable> ReadTablesAsync([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var table in _tables)
            {
                ct.ThrowIfCancellationRequested();
                yield return table;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// The argument reader the module sources share: a JSON object whose members are a closed set, an unknown member
    /// REJECTED rather than ignored (the event-log source's rule - a request that tries <c>{"siteId": 2}</c> is refused, not
    /// quietly answered for the caller's own site).
    /// </summary>
    public static class ExportArguments
    {
        public static bool TryReadObject(string? json, IReadOnlyCollection<string> allowed, out Dictionary<string, JsonElement> members, out string error)
        {
            members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(json))
            {
                return true;
            }

            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    error = "The arguments must be a JSON object.";
                    return false;
                }

                foreach (var member in document.RootElement.EnumerateObject())
                {
                    if (!Contains(allowed, member.Name))
                    {
                        error = $"'{member.Name}' is not an argument of this source.";
                        return false;
                    }

                    members[member.Name] = member.Value.Clone();
                }

                return true;
            }
            catch (JsonException)
            {
                error = "The arguments are not valid JSON.";
                return false;
            }
        }

        public static bool TryGuid(Dictionary<string, JsonElement> members, string name, out Guid value)
        {
            value = Guid.Empty;
            return members.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out value) && value != Guid.Empty;
        }

        private static bool Contains(IReadOnlyCollection<string> allowed, string name)
        {
            foreach (var candidate in allowed)
            {
                if (string.Equals(candidate, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
