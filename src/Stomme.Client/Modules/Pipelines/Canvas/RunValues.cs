using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Canvas
{
    /// <summary>
    /// BL-229 increment 5 - what a run left in the pipeline's variables, read from what its steps produced: for each variable, the
    /// output that the LAST execution of a step saving to it gave. The editor previews an expression with these. They come from
    /// the step records an author is shown (increment 2) - a viewer gets no outputs, so no values - because the engine keeps
    /// nothing better to ask: its variable reader reads the pipeline's own variables, the editor's are its flowchart's, and a
    /// finished run keeps no flowchart context to read them from (measured 2026-10-06).
    /// </summary>
    public static class RunValues
    {
        /// <summary>Variable id to its value as text (JSON for a record or a list); <paramref name="savesOf"/> answers a step's output-to-variable bindings.</summary>
        public static Dictionary<string, string> FromSteps(IEnumerable<PipelineStepRunView> executions, Func<string, IReadOnlyDictionary<string, string>> savesOf)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var execution in (executions ?? Enumerable.Empty<PipelineStepRunView>())
                         .Where(execution => !string.IsNullOrEmpty(execution?.OutputsJson))
                         .OrderBy(execution => execution.StartedAtUtc))
            {
                var saves = savesOf(execution.StepId);
                if (saves is null || saves.Count == 0)
                {
                    continue;
                }

                JsonElement outputs;
                try
                {
                    using var document = JsonDocument.Parse(execution.OutputsJson);
                    outputs = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue; // cut for length, or not JSON: no value to offer
                }

                if (outputs.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var save in saves)
                {
                    if (!string.IsNullOrEmpty(save.Value) && Find(outputs, save.Key) is { } value)
                    {
                        values[save.Value] = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                    }
                }
            }

            return values;
        }

        // An output by its name as the engine records it - either casing.
        private static JsonElement? Find(JsonElement outputs, string name)
        {
            if (outputs.TryGetProperty(name, out var exact))
            {
                return exact;
            }

            foreach (var property in outputs.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value;
                }
            }

            return null;
        }
    }
}
