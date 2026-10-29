#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// One list of records found in a step's data, as rows and columns (the first <see cref="PipelineDataTables.MaxRows"/>), and
    /// its schema - each column's kind and an example (n8n's Schema view).
    /// </summary>
    public sealed record PipelineDataTable(string Name, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, int Total, IReadOnlyList<PipelineDataField> Fields);

    /// <summary>
    /// A field of a list of records: its name, what its values are (<see cref="PipelineDataTables.Kinds"/>) and the first
    /// that is not empty.
    /// </summary>
    public sealed record PipelineDataField(string Name, string Kind, string Example);

    /// <summary>
    /// The lists of records in what a step received or produced (n8n's Table view): each input or output whose value is a
    /// list - as JSON, or as JSON text, which is how the data steps pass records - becomes a table, one column per field in the
    /// order the fields first appear, a list of plain values one column. Nothing else is a table; the JSON is always there too.
    /// </summary>
    public static class PipelineDataTables
    {
        public const int MaxRows = 50;

        public const int MaxColumns = 20;

        public const string ValueColumn = "(value)";

        /// <summary>The tables in a step's data - an object of inputs or outputs by name, or a list on its own; none when it holds no list.</summary>
        public static IReadOnlyList<PipelineDataTable> Of(string? json)
        {
            var root = Parse(json);
            var tables = new List<PipelineDataTable>();
            if (root is JsonObject named)
            {
                foreach (var (name, value) in named)
                {
                    if (Table(name, value) is { } table)
                    {
                        tables.Add(table);
                    }
                }
            }
            else if (Table("", root) is { } table)
            {
                tables.Add(table);
            }

            return tables;
        }

        private static PipelineDataTable? Table(string name, JsonNode? node)
        {
            // JSON text holding a list - how a data step's records reach the next step - is read as the list it is.
            if (node is JsonValue value && value.TryGetValue<string>(out var text) && text.TrimStart().StartsWith('['))
            {
                node = Parse(text);
            }

            if (node is not JsonArray list || list.Count == 0)
            {
                return null;
            }

            var shown = list.Take(MaxRows).ToList();
            if (shown.All(item => item is JsonObject))
            {
                var records = shown.Cast<JsonObject>().ToList();
                var columns = records.SelectMany(record => record.Select(field => field.Key)).Distinct(StringComparer.Ordinal).Take(MaxColumns).ToList();
                var rows = records
                    .Select(record => (IReadOnlyList<string>)columns.Select(column => Cell(record.TryGetPropertyValue(column, out var cell) ? cell : null)).ToList())
                    .ToList();
                var fields = columns.Select(column => Field(column, records.Select(record => record.TryGetPropertyValue(column, out var cell) ? cell : null))).ToList();
                return new PipelineDataTable(name, columns, rows, list.Count, fields);
            }

            if (shown.All(item => item is null or JsonValue))
            {
                return new PipelineDataTable(name, new[] { ValueColumn }, shown.Select(item => (IReadOnlyList<string>)new[] { Cell(item) }).ToList(), list.Count,
                    new[] { Field(ValueColumn, shown) });
            }

            return null;
        }

        /// <summary>What a field's values can be, in the words the Schema view uses.</summary>
        public static readonly IReadOnlyList<string> Kinds = new[] { "text", "number", "number as text", "date", "true/false", "record", "list", "empty", "mixed" };

        // A column's kind - the one kind all its values share, "mixed" when they differ, "empty" when none has one - and the first
        // value that is not empty, cut short.
        private static PipelineDataField Field(string name, IEnumerable<JsonNode?> values)
        {
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            string? example = null;
            foreach (var value in values)
            {
                if (KindOf(value) is { } kind)
                {
                    kinds.Add(kind);
                    example ??= Cell(value);
                }
            }

            var shown = example is null ? "" : example.Length > 60 ? example[..60] + "…" : example;
            return new PipelineDataField(name, kinds.Count switch { 0 => "empty", 1 => kinds.First(), _ => "mixed" }, shown);
        }

        // Null for nothing: no value, or empty text.
        private static string? KindOf(JsonNode? node)
        {
            switch (node)
            {
                case null:
                    return null;
                case JsonObject:
                    return "record";
                case JsonArray:
                    return "list";
            }

            return node.GetValueKind() switch
            {
                JsonValueKind.Number => "number",
                JsonValueKind.True or JsonValueKind.False => "true/false",
                JsonValueKind.String => node.GetValue<string>() is var text && text.Length == 0 ? null
                    : PipelineConditions.IsDate(text) ? "date"
                    : decimal.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) ? "number as text"
                    : "text",
                _ => null,
            };
        }

        /// <summary>
        /// How many items a step's result holds when it is a list - as JSON, as the JSON text the data steps pass, or as a list -
        /// and null when it is no list (n8n's "3 items" on the canvas).
        /// </summary>
        public static int? CountOf(object? value) => value switch
        {
            null => null,
            string text => CountOfText(text),
            JsonElement { ValueKind: JsonValueKind.Array } element => element.GetArrayLength(),
            JsonElement { ValueKind: JsonValueKind.String } element => CountOfText(element.GetString()),
            JsonArray array => array.Count,
            JsonValue node when node.TryGetValue<string>(out var text) => CountOfText(text),
            System.Collections.ICollection collection => collection.Count,
            _ => null,
        };

        private static int? CountOfText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('['))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(text);
                return document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.GetArrayLength() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string Cell(JsonNode? node) => node switch
        {
            null => "",
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            _ => node.ToJsonString(),
        };

        private static JsonNode? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                return null; // cut, or not JSON: no table, the text stays as it is
            }
        }
    }
}
