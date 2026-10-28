#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>
    /// BL-229 - n8n's "When executed by another workflow", with its input defined by fields: where a run another pipeline starts
    /// (Execute or Dispatch pipeline) - or the Run API or MCP - enters with its input. The fields it expects are written as Extract
    /// information's are (<see cref="PipelineFields"/>); what arrives is held to them - each value as its field holds it, a value
    /// that cannot be one failing the run with its name, a required field not sent failing it too - and the record is the result.
    /// With no fields, the input as it came. The caller's editor reads the fields to say what to send.
    /// </summary>
    [Step("Stomme", "Triggers", "Where a run another pipeline starts (Execute or Dispatch pipeline) enters with its input: the fields it expects, each checked and typed - the record is the result.", DisplayName = "When called by another pipeline")]
    public class WhenCalled : CodeStep<string>
    {
        public const string StepTypeName = "Stomme.WhenCalled";

        [Input(UIHint = InputUIHints.MultiLine, Description = "The input it expects, one field per line: a name, then (number), (true or false) or (date) if it is not text, then ': what it is' - 'order (number): the order's number'. Empty: whatever the caller sends.", Order = 1)]
        public Input<string?>? Fields { get; set; }

        [Input(DisplayName = "Required", Description = "The fields a caller must send, separated by commas. Empty: none.", Order = 2)]
        public Input<string?>? RequiredFields { get; set; }

        protected override void Execute(StepExecutionContext context)
        {
            var input = PipelineBuiltIns.InputOf(context.PipelineInput.Select(pair => new KeyValuePair<string, object?>(pair.Key, pair.Value)));
            var written = Fields?.GetOrDefault(context);
            if (string.IsNullOrWhiteSpace(written))
            {
                context.SetResult(JsonSerializer.Serialize(input));
                return;
            }

            var fields = PipelineFields.Parse(written, out var error, "Write at least one field, or leave Fields empty for whatever the caller sends.")
                ?? throw new FaultException("CALL_FIELDS_INVALID", "Pipelines", "Configuration", error!);
            var required = (RequiredFields?.GetOrDefault(context) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (required.FirstOrDefault(name => fields.All(field => !string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))) is { } unknown)
            {
                throw new FaultException("CALL_FIELDS_INVALID", "Pipelines", "Configuration", $"'{unknown}' is required but is none of the fields.");
            }

            var record = new JsonObject();
            foreach (var field in fields)
            {
                var sent = input.FirstOrDefault(pair => string.Equals(pair.Key, field.Name, StringComparison.OrdinalIgnoreCase));
                var given = sent.Key is null ? null : JsonSerializer.SerializeToNode(sent.Value);
                if (!PipelineSiteTables.TryConvert(given, field.Type, out var converted, out _))
                {
                    throw new FaultException("CALL_INPUT_INVALID", "Pipelines", "Business",
                        $"'{PipelineSiteTables.Text(given)}' is not {PipelineSiteTables.Describe(field.Type)}, which '{field.Name}' holds.");
                }

                if (converted is null && required.Any(name => string.Equals(name, field.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new FaultException("CALL_INPUT_MISSING", "Pipelines", "Business", $"The caller sent no '{field.Name}' - it is required.");
                }

                record[field.Name] = converted;
            }

            context.SetResult(record.ToJsonString());
        }
    }
}
