using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Notifications;
using Stomme.Engine.Pipelines.Runtime.Steps;
using Stomme.Engine.Pipelines.Runtime.Stimuli;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Handlers;

/// <summary>
/// Resumes any blocking <see cref="ExecutePipeline"/> steps when its child pipeline completes.
/// </summary>
[UsedImplicitly]
internal class ResumeExecutePipelineStep(IBookmarkQueue bookmarkQueue, IStimulusHasher stimulusHasher) : INotificationHandler<PipelineExecuted>
{
    private static readonly string StepTypeName = StepTypeNameHelper.GenerateTypeName<ExecutePipeline>();

    public async Task HandleAsync(PipelineExecuted notification, CancellationToken cancellationToken)
    {
        var pipelineState = notification.PipelineState;

        if (pipelineState.Status != PipelineStatus.Finished)
            return;

        var props = pipelineState.Properties;
        var waitForCompletion = props.TryGetValue("WaitForCompletion", out var waitForCompletionValue) && (bool)waitForCompletionValue;

        if (!waitForCompletion)
            return;

        var parentInstanceId = (string)props["ParentInstanceId"];
        var stimulus = new ExecutePipelineStimulus(notification.PipelineState.Id);
        // Stomme hand fix (BL-229, 2026-10-08): the child's result under keys of its own, not its output as the parent's input.
        var input = ChildPipelineCompletion.Input(pipelineState);

        var bookmarkQueueItem = new NewBookmarkQueueItem
        {
            PipelineInstanceId = parentInstanceId,
            StepTypeName = StepTypeName,
            StimulusHash = stimulusHasher.Hash(StepTypeName, stimulus),
            Options = new()
            {
                Input = input
            }
        };

        await bookmarkQueue.EnqueueAsync(bookmarkQueueItem, cancellationToken).ConfigureAwait(false);
    }
}