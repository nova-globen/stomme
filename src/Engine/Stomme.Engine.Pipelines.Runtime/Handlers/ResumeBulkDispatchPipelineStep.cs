using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Notifications;
using Stomme.Engine.Pipelines.Runtime.Steps;
using Stomme.Engine.Pipelines.Runtime.Options;
using Stomme.Engine.Pipelines.Runtime.Stimuli;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Handlers;

/// <summary>
/// Resumes any blocking <see cref="BulkDispatchPipelines"/> steps when its child pipelines complete.
/// </summary>
[PublicAPI]
internal class ResumeBulkDispatchPipelineStep(IBookmarkQueue bookmarkQueue, IStimulusHasher stimulusHasher) : INotificationHandler<PipelineExecuted>
{
    public async Task HandleAsync(PipelineExecuted notification, CancellationToken cancellationToken)
    {
        var pipelineState = notification.PipelineState;

        if (pipelineState.Status != PipelineStatus.Finished)
            return;

        var waitForCompletion = pipelineState.Properties.TryGetValue("WaitForCompletion", out var waitForCompletionValue) && (bool)waitForCompletionValue;
        
        if (!waitForCompletion)
            return;
        
        if (!pipelineState.Properties.TryGetValue("ParentInstanceId", out var parentInstanceIdValue))
            return;
        
        var parentInstanceId = (string)parentInstanceIdValue;
        var stepTypeName = StepTypeNameHelper.GenerateTypeName<BulkDispatchPipelines>();
        var stimulus = new BulkDispatchPipelinesStimulus(parentInstanceId);
        var stimulusHash = stimulusHasher.Hash(stepTypeName, stimulus);
        var pipelineInstanceId = pipelineState.Id;
        var input = new Dictionary<string, object>
        {
            ["PipelineOutput"] = pipelineState.Output,
            ["PipelineInstanceId"] = pipelineInstanceId,
            ["PipelineStatus"] = pipelineState.Status,
            ["PipelineSubStatus"] = pipelineState.SubStatus,
        };

        var resumeBookmarkOptions = new ResumeBookmarkOptions
        {
            Input = input
        };
        var bookmarkQueueItem = new NewBookmarkQueueItem
        {
            PipelineInstanceId = parentInstanceId,
            StepTypeName = stepTypeName,
            StimulusHash = stimulusHash,
            Options = resumeBookmarkOptions
        };
        await bookmarkQueue.EnqueueAsync(bookmarkQueueItem, cancellationToken).ConfigureAwait(false);
    }
}