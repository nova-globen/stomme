#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Models.Pipelines;
using Microsoft.Extensions.AI;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>One field Extract information asks for: its name, what it holds, and what it is.</summary>
    public sealed record ExtractedField(string Name, PipelineTableColumnType Type, string Description);

    /// <summary>
    /// Extract information (n8n's Information Extractor): the site's language model reads a text and answers the fields
    /// the author named - each a name, what it holds (text, a number, true or false, a date) and what it is - as one record, a field
    /// the text does not give null. The answer is held to the fields: keys it was not asked for are dropped, a value converted to
    /// what its field holds (a value that will not convert is null, and the log says so - "$12.50" is no number to a table). The
    /// call is Ask AI's: the site's model, quota-gated and metered (<see cref="AiModelCall"/>).
    /// </summary>
    [FlowNode(DoneOutcome, FailedOutcome)]
    [Step("Stomme", "AI", "Have the site's language model pull named fields out of a text, as one record.", DisplayName = "Extract information")]
    public partial class ExtractInformation : DataStep
    {
        [Input(UIHint = InputUIHints.MultiLine, Description = "The text to read. Empty: the previous step's result.", Order = 1)]
        public Input<string?>? Text { get; set; }

        [Input(UIHint = InputUIHints.MultiLine, Description = "One per line: name, or name (number | true or false | date | text), then ': what it is' - e.g. 'total (number): the order's total'.", Order = 2)]
        public Input<string> Fields { get; set; } = null!;

        [Input(UIHint = InputUIHints.MultiLine, Description = "Anything more the model should know - the kind of text, how to read it.", Order = 3)]
        public Input<string?>? Instructions { get; set; }

        [Output(DisplayName = "Missing count", Description = "How many fields the answer left empty.")]
        public Output<int>? MissingCount { get; set; }

        protected override async ValueTask RunAsync(StepExecutionContext context)
        {
            var text = TextOrLastResult(context, Text);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new FaultException("INPUT_REQUIRED", "Pipelines", "Configuration", "The step needs its 'Text' input, or a previous step's result to read.");
            }

            var fields = ParseFields(Required(context, Fields, "Fields"), out var fieldsError);
            if (fields is null)
            {
                await FailAsync(context, "FIELDS_INVALID", fieldsError!).ConfigureAwait(false);
                return;
            }

            var messages = new List<ChatMessage> { new(ChatRole.System, SystemPrompt(fields, Instructions?.GetOrDefault(context))), new(ChatRole.User, text) };
            var (answer, code, error) = await AiModelCall.AskAsync(context, messages, json: true).ConfigureAwait(false);
            if (answer is null)
            {
                await FailAsync(context, code!, error!).ConfigureAwait(false);
                return;
            }

            if (AiModelCall.Object(answer, out var notJson) is not { } given)
            {
                await FailAsync(context, "AI_NOT_JSON", notJson!).ConfigureAwait(false);
                return;
            }

            var (record, missing, unreadable) = Hold(given, fields);
            if (unreadable.Count > 0)
            {
                context.AddExecutionLogEntry("Unreadable", "Left empty, the answer not being what the field holds: " + string.Join(", ", unreadable) + ".");
            }

            context.Set(MissingCount, missing);
            await SucceedAsync(context, record.ToJsonString()).ConfigureAwait(false);
        }

        /// <summary>The fields as the author wrote them, one per line (<see cref="PipelineFields"/>); null, with the reason, for a line that is none.</summary>
        public static List<ExtractedField>? ParseFields(string? text, out string? error) =>
            PipelineFields.Parse(text, out error, "The step needs at least one field to extract.")?.Select(field => new ExtractedField(field.Name, field.Type, field.Description)).ToList();

        /// <summary>The answer held to the fields: each one's value as its field holds it (null when absent or unreadable), nothing else.</summary>
        public static (JsonObject Record, int Missing, List<string> Unreadable) Hold(JsonObject answer, IReadOnlyList<ExtractedField> fields)
        {
            var record = new JsonObject();
            var unreadable = new List<string>();
            foreach (var field in fields)
            {
                var given = answer.FirstOrDefault(property => string.Equals(property.Key, field.Name, StringComparison.OrdinalIgnoreCase)).Value;
                if (!PipelineSiteTables.TryConvert(given, field.Type, out var converted, out _))
                {
                    unreadable.Add($"{field.Name} ('{PipelineSiteTables.Text(given)}')");
                    converted = null;
                }

                record[field.Name] = converted;
            }

            return (record, record.Count(property => property.Value is null), unreadable);
        }

        private static string SystemPrompt(IReadOnlyList<ExtractedField> fields, string? instructions)
        {
            var prompt = new StringBuilder();
            prompt.AppendLine("You extract information from the text the user gives. Answer with ONE JSON object and nothing else, with exactly these keys:");
            foreach (var field in fields)
            {
                prompt.Append("- \"").Append(field.Name).Append("\": ").Append(PipelineSiteTables.Describe(field.Type));
                if (field.Type == PipelineTableColumnType.Date)
                {
                    prompt.Append(" (ISO 8601, like 2026-10-07 or 2026-10-07T14:30:00Z)");
                }

                if (field.Description.Length > 0)
                {
                    prompt.Append(" - ").Append(field.Description);
                }

                prompt.AppendLine();
            }

            prompt.AppendLine("Use null for a key the text does not give. Do not guess, and add no other key.");
            if (!string.IsNullOrWhiteSpace(instructions))
            {
                prompt.AppendLine().AppendLine(instructions.Trim());
            }

            return prompt.ToString();
        }
    }
}
