#nullable enable
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Pipelines.Runtime;
using Stomme.Engine.Pipelines.Runtime.Filters;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// A finished run's answer - the result of the last step it completed (its containers aside) - as the AI agent's tools
    /// and the site's chats give it back: text as it is, anything else as its JSON. A stored run gives results back as JSON
    /// elements, so a string element is its text, not JSON quoting it (found driving: an answer arrived quoted twice).
    /// </summary>
    public static class PipelineRunResults
    {
        public static async Task<string?> LastResultAsync(IStepExecutionStore steps, string runId, CancellationToken ct)
        {
            var records = await steps.FindManyAsync(new StepExecutionRecordFilter { PipelineInstanceId = runId }, ct).ConfigureAwait(false);
            var last = records
                .Where(record => record.CompletedAt is not null && record.Outputs is not null && record.Outputs.TryGetValue("Result", out var value) && value is not null
                    && record.StepType is not ("Stomme.Flowchart" or "Stomme.Pipeline" or "Stomme.Sequence"))
                .OrderBy(record => record.CompletedAt)
                .LastOrDefault();
            return Text(last?.Outputs!["Result"]);
        }

        /// <summary>A result as text; null for none.</summary>
        public static string? Text(object? value) => value switch
        {
            null => null,
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
            JsonElement element => element.GetRawText(),
            _ => JsonSerializer.Serialize(value),
        };
    }
}
