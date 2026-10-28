#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Stomme.Models.Pipelines
{
    /// <summary>One field a step asks for: its name, what it holds, and what it is.</summary>
    public sealed record PipelineField(string Name, PipelineTableColumnType Type, string Description);

    /// <summary>
    /// BL-229 - fields as an author writes them, one per line: a name, then (number), (true or false) or (date) if it is not text,
    /// then ': what it is' - 'total (number): the amount to pay'. Extract information asks the model for them, When called by
    /// another pipeline expects them from its caller, and the editor reads them to say what a sub-pipeline expects. One reading.
    /// </summary>
    public static partial class PipelineFields
    {
        /// <summary>The fields as written; null, with the reason, for a line that is none, a name twice, or no field at all.</summary>
        public static List<PipelineField>? Parse(string? text, out string? error, string noneError = "The step needs at least one field.")
        {
            error = null;
            var fields = new List<PipelineField>();
            foreach (var line in (text ?? "").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0))
            {
                var match = FieldLine().Match(line);
                if (!match.Success || !PipelineSiteVariables.IsName(match.Groups["name"].Value))
                {
                    error = $"'{line}' is not a field: a name (a letter or '_', then letters, digits or '_'), then (number), (true or false), (date) or (text) if it is not text, then ': what it is'.";
                    return null;
                }

                if (TypeOf(match.Groups["type"].Value) is not { } type)
                {
                    error = $"'{match.Groups["type"].Value}' is not what a field holds: text, number, true or false, or date.";
                    return null;
                }

                if (fields.Any(field => string.Equals(field.Name, match.Groups["name"].Value, StringComparison.OrdinalIgnoreCase)))
                {
                    error = $"The field '{match.Groups["name"].Value}' is there twice.";
                    return null;
                }

                fields.Add(new PipelineField(match.Groups["name"].Value, type, match.Groups["description"].Value.Trim()));
            }

            if (fields.Count == 0)
            {
                error = noneError;
                return null;
            }

            return fields;
        }

        /// <summary>What a field's '(…)' says it holds; null when it is none of them.</summary>
        public static PipelineTableColumnType? TypeOf(string type) => type.Trim().ToLowerInvariant() switch
        {
            "" or "text" or "string" => PipelineTableColumnType.Text,
            "number" or "decimal" or "integer" => PipelineTableColumnType.Number,
            "true or false" or "boolean" or "bool" or "yes or no" => PipelineTableColumnType.Boolean,
            "date" or "datetime" or "date and time" => PipelineTableColumnType.Date,
            _ => null,
        };

        /// <summary>A JSON object with each field and an empty value of its kind - what a caller fills in.</summary>
        public static string Template(IEnumerable<PipelineField> fields)
        {
            var template = new JsonObject();
            foreach (var field in fields)
            {
                template[field.Name] = field.Type switch
                {
                    PipelineTableColumnType.Number => 0,
                    PipelineTableColumnType.Boolean => false,
                    _ => "",
                };
            }

            return template.ToJsonString();
        }

        /// <summary>The fields as one line for a person: 'order (number) - the order's number; customer - who ordered'.</summary>
        public static string Describe(IEnumerable<PipelineField> fields) =>
            string.Join("; ", fields.Select(field => field.Name
                + (field.Type == PipelineTableColumnType.Text ? "" : " (" + PipelineSiteTables.Label(field.Type) + ")")
                + (field.Description.Length > 0 ? " - " + field.Description : "")));

        [GeneratedRegex(@"^(?<name>[^\s(:]+)\s*(\((?<type>[^)]*)\))?\s*(:(?<description>.*))?$")]
        private static partial Regex FieldLine();
    }
}
