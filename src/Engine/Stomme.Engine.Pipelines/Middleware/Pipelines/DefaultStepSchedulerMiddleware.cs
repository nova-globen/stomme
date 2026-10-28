using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.CommitStates;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.Pipelines.PipelineExecution;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Pipelines.Middleware.Pipelines;

/// <summary>
/// Installs middleware that executes scheduled work items.
/// </summary>
public static class UseStepSchedulerMiddlewareExtensions
{
    /// <summary>
    /// Installs middleware that executes scheduled work items. 
    /// </summary>
    public static IPipelineExecutionPipelineBuilder UseDefaultStepScheduler(this IPipelineExecutionPipelineBuilder pipelineBuilder) => pipelineBuilder.UseMiddleware<DefaultStepSchedulerMiddleware>();
}

/// <summary>
/// A pipeline execution middleware component that executes scheduled work items.
/// </summary>
public class DefaultStepSchedulerMiddleware(PipelineMiddlewareDelegate next, IStepInvoker stepInvoker, ICommitStrategyRegistry commitStrategyRegistry, IOptions<CommitStateOptions> commitStateOptions) : PipelineExecutionMiddleware(next)
{
    /// <inheritdoc />
    public override async ValueTask InvokeAsync(PipelineExecutionContext context)
    {
        var scheduler = context.Scheduler;

        context.TransitionTo(PipelineSubStatus.Executing);
        await ConditionallyCommitStateAsync(context, PipelineLifetimeEvent.PipelineExecuting).ConfigureAwait(false);

        while (scheduler.HasAny)
        {
            // Do not start a pipeline if cancellation has been requested.
            if (context.CancellationToken.IsCancellationRequested)
                break;

            var currentWorkItem = scheduler.Take();
            await ExecuteWorkItemAsync(context, currentWorkItem).ConfigureAwait(false);
        }

        await Next(context).ConfigureAwait(false);

        if (context.Status == PipelineStatus.Running)
            context.TransitionTo(context.AllStepsCompleted() ? PipelineSubStatus.Finished : PipelineSubStatus.Suspended);
    }

    private async Task ExecuteWorkItemAsync(PipelineExecutionContext context, StepWorkItem workItem)
    {
        var options = new StepInvocationOptions
        {
            Owner = workItem.Owner,
            ExistingStepExecutionContext = workItem.ExistingStepExecutionContext,
            Tag = workItem.Tag,
            Variables = workItem.Variables,
            Input = workItem.Input,
            SchedulingStepExecutionId = workItem.SchedulingStepExecutionId,
            SchedulingPipelineInstanceId = workItem.SchedulingPipelineInstanceId,
            SchedulingCallStackDepth = workItem.SchedulingCallStackDepth
        };

        await stepInvoker.InvokeAsync(context, workItem.Step, options).ConfigureAwait(false);
    }

    private async Task ConditionallyCommitStateAsync(PipelineExecutionContext context, PipelineLifetimeEvent lifetimeEvent)
    {
        var strategyName = context.Pipeline.Options.CommitStrategyName;
        IPipelineCommitStrategy? strategy = !string.IsNullOrWhiteSpace(strategyName)
            ? commitStrategyRegistry.FindPipelineStrategy(strategyName)
            : commitStateOptions.Value.DefaultPipelineCommitStrategy;

        if (strategy == null)
            return;

        var strategyContext = new PipelineCommitStateStrategyContext(context, lifetimeEvent);
        var commitAction = strategy.ShouldCommit(strategyContext);

        if (commitAction is CommitAction.Commit)
            await context.CommitAsync().ConfigureAwait(false);
    }
}