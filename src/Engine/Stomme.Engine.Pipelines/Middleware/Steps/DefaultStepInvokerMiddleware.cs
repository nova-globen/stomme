using System.Reflection;
using Stomme.Engine.Extensions;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.CommitStates;
using Stomme.Engine.Pipelines.Pipelines.StepExecution;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Pipelines.Middleware.Steps;

/// <summary>
/// Provides extension methods to <see cref="IStepExecutionPipelineBuilder"/>.
/// </summary>
public static class StepInvokerMiddlewareExtensions
{
    /// <summary>
    /// Adds the <see cref="DefaultStepInvokerMiddleware"/> component to the pipeline.
    /// </summary>
    public static IStepExecutionPipelineBuilder UseDefaultStepInvoker(this IStepExecutionPipelineBuilder pipelineBuilder) => pipelineBuilder.UseMiddleware<DefaultStepInvokerMiddleware>();
}

/// <summary>
/// A default step execution middleware component that evaluates the current step's properties, executes the step and adds any produced bookmarks to the pipeline execution context.
/// </summary>
public class DefaultStepInvokerMiddleware(StepMiddlewareDelegate next, ICommitStrategyRegistry commitStrategyRegistry, IOptions<CommitStateOptions> commitStateOptions, ILogger<DefaultStepInvokerMiddleware> logger)
    : IStepExecutionMiddleware
{
    private static readonly MethodInfo ExecuteAsyncMethodInfo = typeof(IStep).GetMethod(nameof(IStep.ExecuteAsync))!;

    /// <inheritdoc />
    public async ValueTask InvokeAsync(StepExecutionContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        var pipelineExecutionContext = context.PipelineExecutionContext;

        // Evaluate input properties.
        await EvaluateInputPropertiesAsync(context).ConfigureAwait(false);

        // Prevent the step from being started if cancellation is requested.
        if (context.CancellationToken.IsCancellationRequested)
        {
            context.TransitionTo(StepStatus.Canceled);
            context.AddExecutionLogEntry("Step cancelled");
            return;
        }

        // Check if the step can be executed.
        if (!await context.Step.CanExecuteAsync(context).ConfigureAwait(false))
        {
            context.TransitionTo(StepStatus.Pending);
            context.AddExecutionLogEntry("Precondition Failed", "Cannot execute at this time");
            return;
        }

        // Mark pipeline and step as executing.
        using var executionState = context.EnterExecution();

        // Conditionally commit the pipeline state.
        if (ShouldCommit(context, StepLifetimeEvent.StepExecuting))
            await context.PipelineExecutionContext.CommitAsync().ConfigureAwait(false);

        var previousStepStatus = context.Status;
        context.TransitionTo(StepStatus.Running);

        // Execute step.
        await ExecuteStepAsync(context).ConfigureAwait(false);

        var currentStepStatus = context.Status;
        var stepDidComplete = previousStepStatus != StepStatus.Completed && currentStepStatus == StepStatus.Completed;

        // Reset execute delegate.
        pipelineExecutionContext.ExecuteDelegate = null;

        // If a bookmark was used to resume, burn it if not burnt already by the step.
        var resumedBookmark = pipelineExecutionContext.ResumedBookmarkContext?.Bookmark;

        if (resumedBookmark is { AutoBurn: true })
        {
            logger.LogDebug("Auto-burning bookmark {BookmarkId}", resumedBookmark.Id);
            pipelineExecutionContext.Bookmarks.Remove(resumedBookmark);
        }

        // Update execution count.
        context.IncrementExecutionCount();

        // Invoke next middleware.
        await next(context).ConfigureAwait(false);

        // If the step completed, send a notification.
        if (stepDidComplete)
        {
            var mediator = context.GetRequiredService<INotificationSender>();
            await mediator.SendAsync(new Notifications.StepCompleted(context), context.CancellationToken).ConfigureAwait(false);
        }

        // Conditionally commit the pipeline state.
        if (ShouldCommit(context, StepLifetimeEvent.StepExecuted))
            await context.PipelineExecutionContext.CommitAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Executes the step using the specified context.
    /// This method is virtual so that modules might override this implementation to do things like e.g. asynchronous processing.
    /// </summary>
    protected virtual async ValueTask ExecuteStepAsync(StepExecutionContext context)
    {
        var executeDelegate = context.PipelineExecutionContext.ExecuteDelegate ?? (ExecuteStepDelegate)Delegate.CreateDelegate(typeof(ExecuteStepDelegate), context.Step, ExecuteAsyncMethodInfo);
        await executeDelegate(context).ConfigureAwait(false);
    }

    private async Task EvaluateInputPropertiesAsync(StepExecutionContext context)
    {
        // Evaluate containing composite input properties, if any.
        var compositeContainerContexts = context.GetAncestors().Where(x => x.Step is Composite).ToList();

        foreach (var stepExecutionContext in compositeContainerContexts)
        {
            if (!stepExecutionContext.GetHasEvaluatedProperties())
                await stepExecutionContext.EvaluateInputPropertiesAsync().ConfigureAwait(false);
        }

        // Evaluate input properties.
        await context.EvaluateInputPropertiesAsync().ConfigureAwait(false);
    }

    private bool ShouldCommit(StepExecutionContext context, StepLifetimeEvent lifetimeEvent)
    {
        var strategyName = context.Step.GetCommitStrategy();

        IStepCommitStrategy? strategy = !string.IsNullOrWhiteSpace(strategyName)
            ? commitStrategyRegistry.FindStepStrategy(strategyName)
            : commitStateOptions.Value.DefaultStepCommitStrategy;

        var commitAction = CommitAction.Default;

        if (strategy != null)
        {
            var strategyContext = new StepCommitStateStrategyContext(context, lifetimeEvent);
            commitAction = strategy.ShouldCommit(strategyContext);
        }

        switch (commitAction)
        {
            case CommitAction.Skip:
                return false;
            case CommitAction.Commit:
                return true;
            case CommitAction.Default:
                {
                    var pipelineStrategyName = context.PipelineExecutionContext.Pipeline.Options.CommitStrategyName;

                    IPipelineCommitStrategy? pipelineStrategy = !string.IsNullOrWhiteSpace(pipelineStrategyName)
                        ? commitStrategyRegistry.FindPipelineStrategy(pipelineStrategyName)
                        : commitStateOptions.Value.DefaultPipelineCommitStrategy;

                    if (pipelineStrategy == null)
                        return false;

                    var pipelineLifetimeEvent = lifetimeEvent == StepLifetimeEvent.StepExecuting ? PipelineLifetimeEvent.StepExecuting : PipelineLifetimeEvent.StepExecuted;
                    var pipelineCommitStateStrategyContext = new PipelineCommitStateStrategyContext(context.PipelineExecutionContext, pipelineLifetimeEvent);
                    commitAction = pipelineStrategy.ShouldCommit(pipelineCommitStateStrategyContext);

                    return commitAction == CommitAction.Commit;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(commitAction), commitAction, "Unknown commit action");
        }
    }
}