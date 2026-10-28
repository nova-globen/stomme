using System.Text.Json;
using Stomme.Engine.Extensions;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.CommitStates;
using Stomme.Engine.Pipelines.Middleware.Steps;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.Pipelines.StepExecution;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using Stomme.Engine.Pipelines.Runtime.Stimuli;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Pipelines.Runtime.Middleware.Steps;

/// <summary>
/// Collects the current step for scheduling for execution from a background job if the step is of kind <see cref="StepKind.Job"/> or <see cref="Task"/>.
/// </summary>
[UsedImplicitly]
public class BackgroundStepInvokerMiddleware(
    StepMiddlewareDelegate next,
    ILogger<BackgroundStepInvokerMiddleware> logger,
    IIdentityGenerator identityGenerator,
    IBackgroundStepScheduler backgroundStepScheduler,
    ICommitStrategyRegistry commitStrategyRegistry,
    IMediator mediator,
    IOptions<CommitStateOptions> commitStateOptions)
    : DefaultStepInvokerMiddleware(next, commitStrategyRegistry, commitStateOptions, logger)
{
    internal static string GetBackgroundStepOutputKey(string stepNodeId) => $"__BackgroundStepOutput:{stepNodeId}";
    internal static string GetBackgroundStepOutcomesKey(string stepNodeId) => $"__BackgroundStepOutcomes:{stepNodeId}";
    internal static string GetBackgroundStepCompletedKey(string stepNodeId) => $"__BackgroundStepCompleted:{stepNodeId}";
    internal static string GetBackgroundStepJournalDataKey(string stepNodeId) => $"__BackgroundStepJournalData:{stepNodeId}";
    internal static string GetBackgroundStepScheduledStepsKey(string stepNodeId) => $"__BackgroundStepScheduledSteps:{stepNodeId}";
    internal static string GetBackgroundStepBookmarksKey(string stepNodeId) => $"__BackgroundStepBookmarks:{stepNodeId}";
    internal static string GetBackgroundStepPropertiesKey(string stepNodeId) => $"__BackgroundStepProperties:{stepNodeId}";
    internal const string BackgroundStepBookmarkName = "BackgroundStep";

    /// <inheritdoc />
    protected override async ValueTask ExecuteStepAsync(StepExecutionContext context)
    {
        var shouldRunInBackground = GetShouldRunInBackground(context);

        if (shouldRunInBackground)
            await ScheduleBackgroundStepAsync(context).ConfigureAwait(false);
        else
        {
            await base.ExecuteStepAsync(context).ConfigureAwait(false);

            // This part is either executed from the background or in the foreground when the step is resumed.
            var isResuming = !GetIsBackgroundExecution(context) && context.StepDescriptor.Kind is StepKind.Task or StepKind.Job;
            if (isResuming)
            {
                CaptureOutputIfAny(context);
                CaptureJournalData(context);
                CaptureBookmarkData(context);
                CapturePropertiesIfAny(context);
                await CompleteBackgroundStepOutcomesAsync(context).ConfigureAwait(false);
                await CompleteBackgroundStepAsync(context).ConfigureAwait(false);
                await CompleteBackgroundStepScheduledStepsAsync(context).ConfigureAwait(false);
                await mediator.SendAsync(new BackgroundStepExecutionCompleted(context), context.CancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Schedules the current step for execution in the background.
    /// </summary>
    private async Task ScheduleBackgroundStepAsync(StepExecutionContext context)
    {
        var cancellationToken = context.CancellationToken;
        var pipelineInstanceId = context.PipelineExecutionContext.Id;
        var stepNodeId = context.NodeId;
        var bookmarkId = identityGenerator.GenerateId();
        var scheduledBackgroundStep = new ScheduledBackgroundStep(pipelineInstanceId, stepNodeId, bookmarkId);
        var jobId = await backgroundStepScheduler.CreateAsync(scheduledBackgroundStep, cancellationToken).ConfigureAwait(false);
        var stimulus = new BackgroundStepStimulus
        {
            JobId = jobId
        };
        var bookmarkOptions = new CreateBookmarkArgs
        {
            BookmarkId = bookmarkId,
            BookmarkName = BackgroundStepBookmarkName,
            Stimulus = stimulus,
            AutoComplete = false
        };
        context.CreateBookmark(bookmarkOptions);

        context.DeferTask(async () =>
        {
            await backgroundStepScheduler.ScheduleAsync(jobId, cancellationToken).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Determines whether the current step should be executed in the background.
    /// </summary>
    private static bool GetShouldRunInBackground(StepExecutionContext context)
    {
        var step = context.Step;
        var stepDescriptor = context.StepDescriptor;
        var kind = stepDescriptor.Kind;

        return !GetIsBackgroundExecution(context)
               && context.PipelineExecutionContext.ExecuteDelegate == null
               && (kind is StepKind.Job || GetTaskRunAsynchronously(context));
    }

    private static bool GetTaskRunAsynchronously(StepExecutionContext context)
    {
        var step = context.Step;
        var stepDescriptor = context.StepDescriptor;
        var kind = stepDescriptor.Kind;

        if (kind is not StepKind.Task)
            return false;
        
        var runAsynchronously = step.GetRunAsynchronously();
        return runAsynchronously ?? stepDescriptor.RunAsynchronously;
    }

    private static bool GetIsBackgroundExecution(StepExecutionContext context) => context.TransientProperties.ContainsKey(BackgroundStepExecutionContextExtensions.IsBackgroundExecution);

    /// <summary>
    /// If the input contains captured output from the background step invoker, apply that to the execution context.
    /// </summary>
    private static void CaptureOutputIfAny(StepExecutionContext context)
    {
        var step = context.Step;
        var inputKey = GetBackgroundStepOutputKey(step.NodeId);
        var capturedOutput = context.PipelineExecutionContext.GetProperty<IDictionary<string, object>>(inputKey);

        context.PipelineExecutionContext.Properties.Remove(inputKey);

        if (capturedOutput == null)
            return;

        foreach (var outputEntry in capturedOutput)
        {
            var outputDescriptor = context.StepDescriptor.Outputs.FirstOrDefault(x => x.Name == outputEntry.Key);

            if (outputDescriptor == null)
                continue;

            var output = (Output?)outputDescriptor.ValueGetter(step);
            context.Set(output, outputEntry.Value, outputDescriptor.Name);
        }
    }

    private void CaptureJournalData(StepExecutionContext context)
    {
        var step = context.Step;
        var journalDataKey = GetBackgroundStepJournalDataKey(step.NodeId);
        var journalData = context.PipelineExecutionContext.GetProperty<IDictionary<string, object>>(journalDataKey);

        context.PipelineExecutionContext.Properties.Remove(journalDataKey);

        if (journalData == null)
            return;

        foreach (var journalEntry in journalData)
            context.JournalData[journalEntry.Key] = journalEntry.Value;
    }

    private void CaptureBookmarkData(StepExecutionContext context)
    {
        var step = context.Step;
        var bookmarksKey = GetBackgroundStepBookmarksKey(step.NodeId);
        var bookmarks = context.PipelineExecutionContext.GetProperty<ICollection<Bookmark>>(bookmarksKey);
        if (bookmarks != null)
        {
            context.AddBookmarks(bookmarks);
        }

        context.PipelineExecutionContext.Properties.Remove(bookmarksKey);
    }

    private void CapturePropertiesIfAny(StepExecutionContext context)
    {
        var step = context.Step;
        var propertiesKey = GetBackgroundStepPropertiesKey(step.NodeId);
        var capturedProperties = context.PipelineExecutionContext.GetProperty<IDictionary<string, object>>(propertiesKey);

        context.PipelineExecutionContext.Properties.Remove(propertiesKey);

        if (capturedProperties == null)
            return;

        foreach (var property in capturedProperties)
            context.Properties[property.Key] = property.Value;
    }

    private async Task CompleteBackgroundStepOutcomesAsync(StepExecutionContext context)
    {
        var outcomesKey = GetBackgroundStepOutcomesKey(context.NodeId);
        var outcomes = context.PipelineExecutionContext.GetProperty<ICollection<string>>(outcomesKey);

        if (outcomes != null)
        {
            await context.CompleteStepWithOutcomesAsync(outcomes.ToArray()).ConfigureAwait(false);
        }

        // Remove the outcomes from the pipeline execution context.
        context.PipelineExecutionContext.Properties.Remove(outcomesKey);
    }

    private async Task CompleteBackgroundStepAsync(StepExecutionContext context)
    {
        var completedKey = GetBackgroundStepCompletedKey(context.NodeId);
        var completed = context.PipelineExecutionContext.GetProperty<bool?>(completedKey);

        if (completed is true)
        {
            await context.CompleteStepAsync().ConfigureAwait(false);
        }

        // Remove the outcomes from the pipeline execution context.
        context.PipelineExecutionContext.Properties.Remove(completedKey);
    }

    private async Task CompleteBackgroundStepScheduledStepsAsync(StepExecutionContext context)
    {
        var scheduledStepsKey = GetBackgroundStepScheduledStepsKey(context.NodeId);
        var scheduledStepsJson = context.PipelineExecutionContext.GetProperty<string>(scheduledStepsKey);
        var scheduledSteps = scheduledStepsJson != null ? JsonSerializer.Deserialize<ICollection<ScheduledStep>>(scheduledStepsJson) : null;

        if (scheduledSteps != null)
        {
            foreach (var scheduledStep in scheduledSteps)
            {
                var stepNode = scheduledStep.StepNodeId != null ? context.PipelineExecutionContext.FindStepByNodeId(scheduledStep.StepNodeId) : null;
                var owner = scheduledStep.OwnerStepInstanceId != null ? context.PipelineExecutionContext.StepExecutionContexts.FirstOrDefault(x => x.Id == scheduledStep.OwnerStepInstanceId) : null;
                var options = scheduledStep.Options != null
                    ? new ScheduleWorkOptions
                    {
                        ExistingStepExecutionContext = scheduledStep.Options.ExistingStepInstanceId != null ? context.PipelineExecutionContext.StepExecutionContexts.FirstOrDefault(x => x.Id == scheduledStep.Options.ExistingStepInstanceId) : null,
                        Variables = scheduledStep.Options?.Variables,
                        CompletionCallback = !string.IsNullOrEmpty(scheduledStep.Options?.CompletionCallback) && owner != null ? owner.Step.GetStepCompletionCallback(scheduledStep.Options.CompletionCallback) : null,
                        PreventDuplicateScheduling = scheduledStep.Options?.PreventDuplicateScheduling ?? false,
                        Input = scheduledStep.Options?.Input,
                        Tag = scheduledStep.Options?.Tag
                    }
                    : null;
                await context.ScheduleStepAsync(stepNode, owner, options).ConfigureAwait(false);
            }
        }

        // Remove the scheduled steps from the pipeline execution context.
        context.PipelineExecutionContext.Properties.Remove(scheduledStepsKey);
    }
}