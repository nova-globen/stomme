#nullable enable
using System.Threading.Tasks;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.UIHints;
using Stomme.Models.Pipelines;

namespace Stomme.Modules.Pipelines.Steps
{
    /// <summary>The stimulus a "Pipeline failed" trigger is indexed and fired by: the watched pipeline's definition id, or every pipeline.</summary>
    public sealed record PipelineFailedStimulus(string Pipeline);

    /// <summary>
    /// n8n's error workflow, as an engine TRIGGER (BL-229): a published pipeline that starts with it is indexed under the
    /// pipeline it watches - one, or every pipeline on the site - and when a run of a watched pipeline FAILS,
    /// <c>Triggers/PipelineFailures.cs</c> fires it on that run's site through the engine's stimulus dispatcher. The result is
    /// the failed run as JSON text (<see cref="PipelineFailure"/>): the pipeline, the run, the step that failed and its error.
    /// A run of a pipeline that starts this way fires nothing when it fails, so a failure handler that fails cannot loop.
    /// </summary>
    [Step("Stomme", "Triggers", "Start the pipeline when a run of another pipeline on the site fails - n8n's error workflow.", DisplayName = "Pipeline failed")]
    public class PipelineFailed : Trigger<string>
    {
        public const string FailureInputKey = "__PipelineFailure";

        [Input(DisplayName = "Pipeline", Description = "The pipeline whose failed runs start this one. Leave it empty for every pipeline on the site.", UIHint = InputUIHints.PipelineDefinitionPicker)]
        public Input<string?> Pipeline { get; set; } = null!;

        protected override object GetTriggerPayload(TriggerIndexingContext context) =>
            new PipelineFailedStimulus(PipelineFailure.Watched(Pipeline is null ? null : context.Get(Pipeline)));

        // A trigger is a plain Step, not a CodeStep: nothing completes it but itself (measured - the run sat Suspended).
        protected override async ValueTask ExecuteAsync(StepExecutionContext context)
        {
            context.SetResult(context.TryGetPipelineInput<string>(FailureInputKey, out var failure) ? failure : "");
            await context.CompleteStepAsync().ConfigureAwait(false);
        }
    }
}
