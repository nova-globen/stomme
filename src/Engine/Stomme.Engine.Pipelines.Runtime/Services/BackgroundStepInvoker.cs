using System.Text.Json;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Runtime.Middleware.Steps;
using Stomme.Engine.Pipelines.Runtime.Options;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// A step invoker that invokes steps detached from the pipeline. This is useful for invoking steps from a background worker.
/// </summary>
public class BackgroundStepInvoker(
    IBookmarkQueue bookmarkQueue,
    IPipelineInstanceManager pipelineInstanceManager,
    IPipelineDefinitionService pipelineDefinitionService,
    IVariablePersistenceManager variablePersistenceManager,
    IStepInvoker stepInvoker,
    IStepPropertyLogPersistenceEvaluator stepPropertyLogPersistenceEvaluator,
    PipelineHeartbeatGeneratorFactory pipelineHeartbeatGeneratorFactory,
    IServiceProvider serviceProvider,
    ILogger<BackgroundStepInvoker> logger)
    : IBackgroundStepInvoker
{
    private readonly ILogger _logger = logger;

    /// <inheritdoc />
    public async Task ExecuteAsync(ScheduledBackgroundStep scheduledBackgroundStep, CancellationToken cancellationToken = default)
    {
        var pipelineInstanceId = scheduledBackgroundStep.PipelineInstanceId;
        var pipelineInstance = await pipelineInstanceManager.FindByIdAsync(pipelineInstanceId, cancellationToken).ConfigureAwait(false);
        if (pipelineInstance == null) throw new("Pipeline instance not found");
        var pipelineState = pipelineInstance.PipelineState;
        var pipeline = await pipelineDefinitionService.FindPipelineGraphAsync(pipelineInstance.DefinitionVersionId, cancellationToken).ConfigureAwait(false);
        if (pipeline == null) throw new("Pipeline definition not found");
        var pipelineExecutionContext = await PipelineExecutionContext.CreateAsync(serviceProvider, pipeline, pipelineState, cancellationToken: cancellationToken).ConfigureAwait(false);
        var stepNodeId = scheduledBackgroundStep.StepNodeId;
        var stepExecutionContext = pipelineExecutionContext.StepExecutionContexts.First(x => x.NodeId == stepNodeId);

        using (pipelineHeartbeatGeneratorFactory.CreateHeartbeatGenerator(pipelineExecutionContext))
        {
            await variablePersistenceManager.LoadVariablesAsync(pipelineExecutionContext).ConfigureAwait(false);
            stepExecutionContext.SetIsBackgroundExecution();
            await stepInvoker.InvokeAsync(stepExecutionContext).ConfigureAwait(false);
            await variablePersistenceManager.SaveVariablesAsync(pipelineExecutionContext).ConfigureAwait(false);
        }
        await ResumePipelineAsync(stepExecutionContext, scheduledBackgroundStep).ConfigureAwait(false);
    }

    private async Task ResumePipelineAsync(StepExecutionContext stepExecutionContext, ScheduledBackgroundStep scheduledBackgroundStep)
    {
        var cancellationToken = stepExecutionContext.CancellationToken;
        var stepNodeId = scheduledBackgroundStep.StepNodeId;
        if (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Background execution for step {StepNodeId} was canceled", stepNodeId);
            return;
        }

        var inputKey = BackgroundStepInvokerMiddleware.GetBackgroundStepOutputKey(stepNodeId);
        var outcomesKey = BackgroundStepInvokerMiddleware.GetBackgroundStepOutcomesKey(stepNodeId);
        var completedKey = BackgroundStepInvokerMiddleware.GetBackgroundStepCompletedKey(stepNodeId);
        var journalDataKey = BackgroundStepInvokerMiddleware.GetBackgroundStepJournalDataKey(stepNodeId);
        var bookmarksKey = BackgroundStepInvokerMiddleware.GetBackgroundStepBookmarksKey(stepNodeId);
        var scheduledStepsKey = BackgroundStepInvokerMiddleware.GetBackgroundStepScheduledStepsKey(stepNodeId);
        var propsKey = BackgroundStepInvokerMiddleware.GetBackgroundStepPropertiesKey(stepNodeId);
        var outcomes = stepExecutionContext.GetBackgroundOutcomes()?.ToList();
        var completed = stepExecutionContext.GetBackgroundCompleted();
        var scheduledSteps = stepExecutionContext.GetBackgroundScheduledSteps().ToList();
        var pipelineInstanceId = scheduledBackgroundStep.PipelineInstanceId;
        var outputValues = await stepPropertyLogPersistenceEvaluator.GetPersistableOutputAsync(stepExecutionContext).ConfigureAwait(false);
        var bookmarkProps = new Dictionary<string, object>
        {
            [scheduledStepsKey] = JsonSerializer.Serialize(scheduledSteps),
            [inputKey] = outputValues,
            [journalDataKey] = stepExecutionContext.JournalData,
            [bookmarksKey] = stepExecutionContext.Bookmarks.ToList(),
            [propsKey] = stepExecutionContext.Properties.ToDictionary() // ChangeTrackingDictionary is not persistable, so we need to create a copy of the dictionary.
        };

        if (outcomes != null) bookmarkProps[outcomesKey] = outcomes;
        if (completed != null) bookmarkProps[completedKey] = completed;

        var resumeBookmarkOptions = new ResumeBookmarkOptions
        {
            Properties = bookmarkProps,
        };
        var enqueuedBookmark = new NewBookmarkQueueItem
        {
            PipelineInstanceId = pipelineInstanceId,
            BookmarkId = scheduledBackgroundStep.BookmarkId,
            Options = resumeBookmarkOptions
        };
        await bookmarkQueue.EnqueueAsync(enqueuedBookmark, cancellationToken).ConfigureAwait(false);
    }
}