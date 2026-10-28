using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Runtime.Steps;
using Stomme.Engine.Pipelines.Runtime.Options;
using Stomme.Engine.Pipelines.Runtime.Stimuli;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class TaskReporter(IBookmarkQueue bookmarkQueue, IStimulusHasher stimulusHasher) : ITaskReporter
{
    private static readonly string StepTypeName = StepTypeNameHelper.GenerateTypeName<RunTask>();
    
    /// <inheritdoc />
    public async Task ReportCompletionAsync(string taskId, object? result = null, CancellationToken cancellationToken = default)
    {
        var stimulus = new RunTaskStimulus(taskId, null!);

        var input = new Dictionary<string, object>
        {
            [RunTask.InputKey] = result!
        };

        var bookmarkQueueItem = new NewBookmarkQueueItem
        {
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