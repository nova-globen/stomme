using System.Runtime.CompilerServices;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Stimuli;
using Stomme.Engine.Pipelines.Runtime.UIHints;
using Stomme.Engine.Pipelines.UIHints;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Steps;

/// <summary>
/// Creates a new pipeline instance of the specified pipeline and dispatches it for execution.
/// </summary>
[Step("Stomme", "Composition", "Create a new pipeline instance of the specified pipeline and dispatch it for execution.")]
[UsedImplicitly]
public class DispatchPipeline : Step<object>
{
    /// <inheritdoc />
    public DispatchPipeline([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <summary>
    /// The definition ID of the pipeline to dispatch. 
    /// </summary>
    [Input(
        DisplayName = "Pipeline Definition",
        Description = "The definition ID of the pipeline to dispatch.",
        UIHint = InputUIHints.PipelineDefinitionPicker
    )]
    public Input<string> PipelineDefinitionId { get; set; } = null!;

    /// <summary>
    /// The correlation ID to associate the pipeline with. 
    /// </summary>
    [Input(
        DisplayName = "Correlation ID",
        Description = "The correlation ID to associate the pipeline with."
    )]
    public Input<string?> CorrelationId { get; set; } = null!;

    /// <summary>
    /// The input to send to the pipeline.
    /// </summary>
    [Input(Description = "The input to send to the pipeline.")]
    public Input<IDictionary<string, object>?> Input { get; set; } = null!;

    /// <summary>
    /// True to wait for the child pipeline to complete before completing this step, false to "fire and forget".
    /// </summary>
    [Input(Description = "Wait for the child pipeline to complete before completing this step.")]
    public Input<bool> WaitForCompletion { get; set; } = null!;

    /// <summary>
    /// Indicates whether a new trace context should be started for the pipeline execution.
    /// </summary>
    [Input(Description = "Start a new trace context when using Open Telemetry.", Category = "Open Telemetry")]
    public Input<bool> StartNewTrace { get; set; } = null!;

    /// <summary>
    /// The channel to dispatch the pipeline to.
    /// </summary>
    [Input(
        DisplayName = "Channel",
        Description = "The channel to dispatch the pipeline to.",
        UIHint = InputUIHints.DropDown,
        UIHandler = typeof(DispatcherChannelOptionsProvider)
    )]
    public Input<string?> ChannelName { get; set; } = null!;

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        var waitForCompletion = WaitForCompletion.GetOrDefault(context);

        // Dispatch the child pipeline.
        var instanceId = await DispatchChildPipelineAsync(context, waitForCompletion).ConfigureAwait(false);

        // If we need to wait for the child pipeline to complete, create a bookmark.
        if (waitForCompletion)
        {
            var bookmarkOptions = new CreateBookmarkArgs
            {
                Callback = OnChildPipelineCompletedAsync,
                Stimulus = new DispatchPipelineStimulus(instanceId),
                IncludeStepInstanceId = false
            };
            context.CreateBookmark(bookmarkOptions);
        }
        else
        {
            // Otherwise, we can complete immediately.
            await context.CompleteStepAsync().ConfigureAwait(false);
        }
    }

    private async ValueTask<string> DispatchChildPipelineAsync(StepExecutionContext context, bool waitForCompletion)
    {
        var pipelineDefinitionId = PipelineDefinitionId.Get(context);
        var pipelineDefinitionService = context.GetRequiredService<IPipelineDefinitionService>();
        var pipelineGraph = await pipelineDefinitionService.FindPipelineGraphAsync(pipelineDefinitionId, VersionOptions.Published, context.CancellationToken).ConfigureAwait(false);

        if (pipelineGraph == null)
            throw new($"No published version of pipeline definition with ID {pipelineDefinitionId} found.");

        var input = Input.GetOrDefault(context) ?? new Dictionary<string, object>();
        var channelName = ChannelName.GetOrDefault(context);
        var startNewTrace = StartNewTrace.GetOrDefault(context);
        var parentInstanceId = context.PipelineExecutionContext.Id;
        var properties = new Dictionary<string, object>
        {
            ["ParentInstanceId"] = parentInstanceId,
        };

        // If we need to wait for the child pipeline to complete, set the property. This will be used by the ResumeDispatchPipelineStep handler.
        if (waitForCompletion) properties["WaitForCompletion"] = true;
        if (startNewTrace) properties["StartNewTrace"] = true;
        
        input["ParentInstanceId"] = parentInstanceId;

        var correlationId = CorrelationId.GetOrDefault(context);
        var pipelineDispatcher = context.GetRequiredService<IPipelineDispatcher>();
        var identityGenerator = context.GetRequiredService<IIdentityGenerator>();
        var instanceId = identityGenerator.GenerateId();
        var request = new DispatchPipelineDefinitionRequest(pipelineGraph.Pipeline.Identity.Id)
        {
            ParentPipelineInstanceId = parentInstanceId,
            Input = input,
            Properties = properties,
            CorrelationId = correlationId,
            InstanceId = instanceId,
            SchedulingStepExecutionId = context.Id,
            SchedulingPipelineInstanceId = parentInstanceId,
            SchedulingCallStackDepth = context.CallStackDepth
        };
        var options = new DispatchPipelineOptions
        {
            Channel = channelName
        };

        // Dispatch the child pipeline.
        var dispatchResponse = await pipelineDispatcher.DispatchAsync(request, options, context.CancellationToken).ConfigureAwait(false);
        dispatchResponse.ThrowIfFailed();

        return instanceId;
    }

    private async ValueTask OnChildPipelineCompletedAsync(StepExecutionContext context)
    {
        // Stomme hand fix (BL-229, 2026-10-08): the child's output, not the parent's input it was mixed into (ChildPipelineCompletion).
        context.Set(Result, ChildPipelineCompletion.OutputOf(context.PipelineInput));
        await context.CompleteStepAsync().ConfigureAwait(false);
    }
}