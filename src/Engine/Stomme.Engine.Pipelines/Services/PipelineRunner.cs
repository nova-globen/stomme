using Stomme.Engine.Extensions;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.CommitStates;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Notifications;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.State;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines;

/// <inheritdoc />
public class PipelineRunner(
    IServiceProvider serviceProvider,
    IPipelineExecutionPipeline executionPipeline,
    IPipelineStateExtractor pipelineStateExtractor,
    IPipelineBuilderFactory pipelineBuilderFactory,
    IPipelineGraphBuilder pipelineGraphBuilder,
    IIdentityGenerator identityGenerator,
    INotificationSender notificationSender,
    ILoggerStateGenerator<PipelineExecutionContext> loggerStateGenerator,
    ICommitStateHandler commitStateHandler,
    ILogger<PipelineRunner> logger)
    : IPipelineRunner
{
    /// <inheritdoc />
    public async Task<RunPipelineResult> RunAsync(IStep step, RunPipelineOptions? options = null, CancellationToken cancellationToken = default)
    {
        var pipeline = Pipeline.FromStep(step);
        var pipelineGraph = await pipelineGraphBuilder.BuildAsync(pipeline, cancellationToken).ConfigureAwait(false);
        return await RunAsync(pipelineGraph, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RunPipelineResult> RunAsync(IPipeline pipeline, RunPipelineOptions? options = null, CancellationToken cancellationToken = default)
    {
        var builder = pipelineBuilderFactory.CreateBuilder();
        var pipelineDefinition = await builder.BuildPipelineAsync(pipeline, cancellationToken).ConfigureAwait(false);
        return await RunAsync(pipelineDefinition, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RunPipelineResult<TResult>> RunAsync<TResult>(PipelineBase<TResult> pipeline, RunPipelineOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await RunAsync((IPipeline)pipeline, options, cancellationToken).ConfigureAwait(false);
        return new(result.PipelineExecutionContext, result.PipelineState, result.Pipeline, (TResult)result.Result!, result.Journal);
    }

    /// <inheritdoc />
    public async Task<RunPipelineResult> RunAsync<T>(RunPipelineOptions? options = null, CancellationToken cancellationToken = default) where T : IPipeline, new()
    {
        var builder = pipelineBuilderFactory.CreateBuilder();
        var pipelineDefinition = await builder.BuildPipelineAsync<T>(cancellationToken).ConfigureAwait(false);
        return await RunAsync(pipelineDefinition, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TResult> RunAsync<T, TResult>(RunPipelineOptions? options = null, CancellationToken cancellationToken = default) where T : PipelineBase<TResult>, new()
    {
        var builder = pipelineBuilderFactory.CreateBuilder();
        var pipeline = await builder.BuildPipelineAsync<T>(cancellationToken).ConfigureAwait(false);
        var result = await RunAsync(pipeline, options, cancellationToken).ConfigureAwait(false);
        return (TResult)result.Result!;
    }

    /// <inheritdoc />
    public async Task<RunPipelineResult> RunAsync(PipelineGraph pipelineGraph, RunPipelineOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Set up a pipeline execution context.
        var instanceId = options?.PipelineInstanceId ?? identityGenerator.GenerateId();
        var input = options?.Input;
        var properties = options?.Properties;
        var correlationId = options?.CorrelationId;
        var triggerStepId = options?.TriggerStepId;
        var parentPipelineInstanceId = options?.ParentPipelineInstanceId;
        var pipelineExecutionContext = await PipelineExecutionContext.CreateAsync(
            serviceProvider,
            pipelineGraph,
            instanceId,
            correlationId,
            parentPipelineInstanceId,
            input,
            properties,
            null,
            triggerStepId,
            cancellationToken).ConfigureAwait(false);

        // Schedule the first step.
        pipelineExecutionContext.SchedulePipeline(
            schedulingStepExecutionId: options?.SchedulingStepExecutionId,
            schedulingPipelineInstanceId: options?.SchedulingPipelineInstanceId,
            schedulingCallStackDepth: options?.SchedulingCallStackDepth);

        return await RunAsync(pipelineExecutionContext).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RunPipelineResult> RunAsync(Pipeline pipeline, RunPipelineOptions? options = null, CancellationToken cancellationToken = default)
    {
        var pipelineGraph = await pipelineGraphBuilder.BuildAsync(pipeline, cancellationToken).ConfigureAwait(false);
        return await RunAsync(pipelineGraph, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RunPipelineResult> RunAsync(Pipeline pipeline, PipelineState pipelineState, RunPipelineOptions? options = null, CancellationToken cancellationToken = default)
    {
        var pipelineGraph = await pipelineGraphBuilder.BuildAsync(pipeline, cancellationToken).ConfigureAwait(false);
        return await RunAsync(pipelineGraph, pipelineState, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RunPipelineResult> RunAsync(PipelineGraph pipelineGraph, PipelineState pipelineState, RunPipelineOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Create a pipeline execution context.
        var input = options?.Input;
        var variables = options?.Variables;
        var properties = options?.Properties;
        var correlationId = options?.CorrelationId ?? pipelineState.CorrelationId;
        var triggerStepId = options?.TriggerStepId;
        var parentPipelineInstanceId = options?.ParentPipelineInstanceId;
        var pipelineExecutionContext = await PipelineExecutionContext.CreateAsync(
            serviceProvider,
            pipelineGraph,
            pipelineState,
            correlationId,
            parentPipelineInstanceId,
            input,
            properties,
            null,
            triggerStepId,
            cancellationToken).ConfigureAwait(false);

        var bookmarkId = options?.BookmarkId;
        var stepHandle = options?.StepHandle;

        if (!string.IsNullOrEmpty(bookmarkId))
        {
            var bookmark = pipelineState.Bookmarks.FirstOrDefault(x => x.Id == bookmarkId);

            if (bookmark != null)
                pipelineExecutionContext.ScheduleBookmark(bookmark);
        }
        else if (stepHandle != null)
        {
            if (!string.IsNullOrEmpty(stepHandle.StepInstanceId))
            {
                var stepExecutionContext = pipelineExecutionContext.StepExecutionContexts.FirstOrDefault(x => x.Id == stepHandle.StepInstanceId)
                                               ?? throw new("No step execution context found with the specified ID.");
                pipelineExecutionContext.ScheduleStepExecutionContext(stepExecutionContext);
            }
            else
            {
                var step = pipelineExecutionContext.FindStep(stepHandle);
                if (step != null) pipelineExecutionContext.ScheduleStep(step);
            }
        }
        else if (pipelineExecutionContext.Scheduler.HasAny)
        {
            // Do nothing. The scheduler already has steps to schedule.
        }
        else
        {
            // Check if there are any interrupted steps.
            var interruptedStepExecutionContexts = pipelineExecutionContext.StepExecutionContexts.Where(x => x.IsExecuting).ToList();

            if (interruptedStepExecutionContexts.Count > 0)
            {
                // Schedule the interrupted steps.
                foreach (var pendingStepExecutionContext in interruptedStepExecutionContexts)
                    pipelineExecutionContext.ScheduleStepExecutionContext(pendingStepExecutionContext);
            }
            else
            {
                // Nothing was scheduled. Schedule the pipeline itself.
                var vars = variables?.Select(x => new Variable(x.Key, x.Value)).ToList();
                var schedulingStepExecutionId = options?.SchedulingStepExecutionId;
                var schedulingPipelineInstanceId = options?.SchedulingPipelineInstanceId;
                var schedulingCallStackDepth = options?.SchedulingCallStackDepth;

                pipelineExecutionContext.SchedulePipeline(
                    variables: vars,
                    schedulingStepExecutionId: schedulingStepExecutionId,
                    schedulingPipelineInstanceId: schedulingPipelineInstanceId,
                    schedulingCallStackDepth: schedulingCallStackDepth);
            }
        }

        // Set variables, if any.
        if (variables != null)
        {
            var rootContext = pipelineExecutionContext.StepExecutionContexts.FirstOrDefault(x => x.ParentStepExecutionContext == null);

            if (rootContext != null)
            {
                foreach (var variable in variables)
                    rootContext.SetDynamicVariable(variable.Key, variable.Value);
            }
        }

        return await RunAsync(pipelineExecutionContext).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<RunPipelineResult> RunAsync(PipelineExecutionContext pipelineExecutionContext)
    {
        var loggerState = loggerStateGenerator.GenerateLoggerState(pipelineExecutionContext);
        using var loggingScope = logger.BeginScope(loggerState);
        var pipeline = pipelineExecutionContext.Pipeline;
        var cancellationToken = pipelineExecutionContext.CancellationToken;

        await notificationSender.SendAsync(new PipelineExecuting(pipeline, pipelineExecutionContext), cancellationToken).ConfigureAwait(false);

        // If the status is Pending, it means the pipeline is started for the first time.
        if (pipelineExecutionContext.SubStatus == PipelineSubStatus.Pending)
        {
            pipelineExecutionContext.TransitionTo(PipelineSubStatus.Executing);
            await notificationSender.SendAsync(new PipelineStarted(pipeline, pipelineExecutionContext), cancellationToken).ConfigureAwait(false);
        }

        await executionPipeline.ExecuteAsync(pipelineExecutionContext).ConfigureAwait(false);
        var pipelineState = pipelineStateExtractor.Extract(pipelineExecutionContext);

        if (pipelineState.Status == PipelineStatus.Finished)
        {
            await notificationSender.SendAsync(new PipelineFinished(pipeline, pipelineState, pipelineExecutionContext), cancellationToken).ConfigureAwait(false);
        }

        var result = pipeline.ResultVariable?.Get(pipelineExecutionContext.MemoryRegister);
        var pipelineExecutionLogEntries = pipelineExecutionContext.ExecutionLog.ToList();
        var stepExecutionContexts = pipelineExecutionContext.StepExecutionContexts.ToList();
        var journal = new Journal(pipelineExecutionLogEntries, stepExecutionContexts);
        await notificationSender.SendAsync(new PipelineExecuted(pipeline, pipelineState, pipelineExecutionContext), cancellationToken).ConfigureAwait(false);
        await commitStateHandler.CommitAsync(pipelineExecutionContext, pipelineState, cancellationToken).ConfigureAwait(false);
        return new(pipelineExecutionContext, pipelineState, pipelineExecutionContext.Pipeline, result, journal);
    }
}