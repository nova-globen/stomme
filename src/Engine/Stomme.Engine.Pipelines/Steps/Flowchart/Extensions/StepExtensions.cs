using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps.Flowchart.Steps;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Extensions;

public static class StepExtensions
{
    extension(IStep step)
    {
        public MergeMode? GetMergeMode()
        {
            if (!step.CustomProperties.TryGetValue("mergeMode", out var value))
                return null;

            // Handle both string and enum values for backwards compatibility
            var result = value switch
            {
                MergeMode mode => mode,
                string str when Enum.TryParse<MergeMode>(str, true, out var mode) => mode,
                _ => (MergeMode?)null
            };

            // Treat MergeMode.None as equivalent to null (no merge mode set)
            return result == MergeMode.None ? null : result;
        }

        public void SetMergeMode(MergeMode? value)
        {
            // Treat MergeMode.None as equivalent to null (no merge mode set)
            if (value == null || value == MergeMode.None)
                step.CustomProperties.Remove("mergeMode");
            else
                step.CustomProperties["mergeMode"] = value.ToString()!;
        }

        public async Task<MergeMode?> GetMergeModeAsync(StepExecutionContext context)
        {
            if (step.Type != "Stomme.FlowJoin")
            {
                return step.GetMergeMode();
            }

            // Handle deprecated FlowJoin step by evaluating its JoinMode property and mapping it to the appropriate MergeMode equivalent.
            var joinStepExecutionContext = await context.PipelineExecutionContext.CreateStepExecutionContextAsync(step).ConfigureAwait(false);
            var joinMode = await joinStepExecutionContext.EvaluateInputPropertyAsync<FlowJoin, FlowJoinMode>(x => x.Mode).ConfigureAwait(false);

            return joinMode switch
            {
                FlowJoinMode.WaitAny => MergeMode.Race,
                _ => MergeMode.Merge // WaitAll maps to Merge (wait for all activated branches)
            };
        }
    }
}