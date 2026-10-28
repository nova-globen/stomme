#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// The records shape the data steps share: a JSON array of flat objects (the predecessor platform's JSON payload). A single object
    /// is one record; anything else is refused with the reason, never silently treated as empty.
    /// </summary>
    internal static class Records
    {
        public static bool TryParse(string? json, out List<JsonObject> rows, out string? error)
        {
            rows = new List<JsonObject>();
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                return true;
            }

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(json);
            }
            catch (JsonException ex)
            {
                error = $"The records are not JSON: {ex.Message}";
                return false;
            }

            switch (node)
            {
                case JsonArray array:
                    foreach (var item in array)
                    {
                        if (item is JsonObject row)
                        {
                            rows.Add(row);
                        }
                        else
                        {
                            error = "The records array holds an element that is not an object.";
                            return false;
                        }
                    }

                    return true;
                case JsonObject single:
                    rows.Add(single);
                    return true;
                default:
                    error = "The records are neither a JSON array of objects nor one object.";
                    return false;
            }
        }

        public static string ToJson(IEnumerable<JsonObject> rows) =>
            new JsonArray(rows.Select(row => (JsonNode)row.DeepClone()).ToArray()).ToJsonString();

        /// <summary>A dotted path into a record (<c>address.zip</c>); null when any segment is absent.</summary>
        public static JsonNode? ReadPath(JsonObject row, string path)
        {
            JsonNode? current = row;
            foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (current is JsonObject obj && obj.TryGetPropertyValue(segment, out var next))
                {
                    current = next;
                }
                else
                {
                    return null;
                }
            }

            return ReferenceEquals(current, row) ? null : current;
        }

        /// <summary>A node as text: a scalar's invariant form, an object or array as its JSON, null as null.</summary>
        public static string? Text(JsonNode? node)
        {
            switch (node)
            {
                case null:
                    return null;
                case JsonValue value:
                    if (value.TryGetValue<string>(out var s))
                    {
                        return s;
                    }

                    if (value.TryGetValue<bool>(out var b))
                    {
                        return b ? "true" : "false";
                    }

                    if (value.TryGetValue<decimal>(out var d))
                    {
                        return d.ToString(CultureInfo.InvariantCulture);
                    }

                    if (value.TryGetValue<double>(out var f))
                    {
                        return f.ToString(CultureInfo.InvariantCulture);
                    }

                    return value.ToJsonString();
                default:
                    return node.ToJsonString();
            }
        }

        /// <summary>The columns of a record set: every key, in the order it first appears.</summary>
        public static List<string> Columns(IEnumerable<JsonObject> rows)
        {
            var columns = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                foreach (var property in row)
                {
                    if (seen.Add(property.Key))
                    {
                        columns.Add(property.Key);
                    }
                }
            }

            return columns;
        }

        /// <summary>The non-empty, trimmed lines of a multi-line input; a line starting with <c>#</c> is a comment.</summary>
        public static IEnumerable<string> Lines(string? text) =>
            (text ?? "").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'));
    }
}
