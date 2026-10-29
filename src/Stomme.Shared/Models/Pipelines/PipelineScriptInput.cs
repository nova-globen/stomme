#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Stomme.Models.Pipelines
{
    /// <summary>
    /// What a Run script step receives, as the editor's AI is told it: n8n's Code node sends its AI the SCHEMA of the
    /// node's input, not its data, and so does this - records, plain values, one record or text, and the fields of the records
    /// with what their values are (<see cref="PipelineDataTables.Kinds"/>), never a value.
    /// </summary>
    public static class PipelineScriptInput
    {
        public const string Records = "records";

        public const string Values = "values";

        public const string Record = "record";

        public const string Text = "text";

        /// <summary>What <paramref name="data"/> - a step's result as the editor last saw it - is, and its fields; empty when there is none.</summary>
        public static (string Receives, List<PipelineScriptField> Fields) Describe(string? data)
        {
            if (string.IsNullOrWhiteSpace(data))
            {
                return (string.Empty, new List<PipelineScriptField>());
            }

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(data);
            }
            catch (JsonException)
            {
                return (Text, new List<PipelineScriptField>()); // a step's text, not JSON
            }

            return root switch
            {
                JsonArray => FieldsOf(data),
                JsonObject => (Record, FieldsOf("[" + data + "]").Fields), // one record: its fields, as a list of one would have them
                _ => (Text, new List<PipelineScriptField>()), // a number, a string: text to the script
            };
        }

        private static (string Receives, List<PipelineScriptField> Fields) FieldsOf(string list)
        {
            var table = PipelineDataTables.Of(list).FirstOrDefault();
            if (table is null)
            {
                return (Records, new List<PipelineScriptField>()); // an empty list, or one of lists: records it may hold, none seen
            }

            return table.Fields.Count == 1 && table.Fields[0].Name == PipelineDataTables.ValueColumn
                ? (Values, new List<PipelineScriptField>())
                : (Records, table.Fields.Select(field => new PipelineScriptField { Name = field.Name, Kind = field.Kind }).ToList());
        }
    }
}
