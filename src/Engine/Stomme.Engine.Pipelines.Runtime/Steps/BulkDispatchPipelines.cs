using System.Runtime.CompilerServices;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Helpers;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps.Flowchart.Attributes;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Stimuli;
using Stomme.Engine.Pipelines.Runtime.UIHints;
using Stomme.Engine.Pipelines.UIHints;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Steps;

/// <summary>
/// Creates new pipeline instances of the specified pipeline for each item in the data source and dispatches them for execution.
/// </summary>
[Step("Stomme", "Composition", "Create new pipeline instances for each item in the data source and dispatch them for execution.", Kind = StepKind.Task)]
[FlowNode("Completed", "Canceled", "Done")]
[UsedImplicitly]
public class BulkDispatchPipelines : Step
{
    private const string DispatchedInstancesCountKey = nameof(DispatchedInstancesCountKey);
    private const string CompletedInstancesCountKey = nameof(CompletedInstancesCountKey);

    /// <inheritdoc />
    public BulkDispatchPipelines([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <summary>
    /// The definition ID of the pipelines to dispatch. 
    /// </summary>
    [Input(
        DisplayName = "Pipeline Definition",
        Description = "The definition ID of the pipelines to dispatch.",
        UIHint = InputUIHints.PipelineDefinitionPicker
    )]
    public Input<string> PipelineDefinitionId { get; set; } = null!;

    /// <summary>
    /// The data source to use for dispatching the pipelines.
    /// </summary>
    [Input(Description = "The data source to use for dispatching the pipelines.")]
    public Input<object> Items { get; set; } = null!;

    /// <summary>
    /// The default key to use for the item input. Will not be used if the Items contain a list of dictionaries.
    /// </summary>
    [Input(Description = "The default key to use for the input name when sending the current item to the dispatched pipeline. Will not be used if the Items field contain a list of dictionaries", DefaultValue = "Item")]
    public Input<string> DefaultItemInputKey { get; set; } = new("Item");

    /// <summary>
    /// The correlation ID to associate the pipeline with. 
    /// </summary>
    [Input(
        DisplayName = "Correlation ID Function",
        Description = "A function to compute the correlation ID to associate a dispatched pipeline with.",
        AutoEvaluate = false)]
    public Input<string?>? CorrelationIdFunction { get; set; }

    /// <summary>
    /// The input to send to the pipelines.
    /// </summary>
    [Input(Description = "Additional input to send to the pipelines being dispatched.")]
    public Input<IDictionary<string, object>?> Input { get; set; } = null!;

    /// <summary>
    /// True to wait for the child pipeline to complete before completing this step, false to "fire and forget".
    /// </summary>
    [Input(
        Description = "Wait for the dispatched pipelines to complete before completing this step.",
        DefaultValue = true)]
    public Input<bool> WaitForCompletion { get; set; } = new(true);

    /// <summary>
    /// Indicates whether a new trace context should be started for the pipeline execution.
    /// </summary>
    [Input(Description = "Start a new trace context when using Open Telemetry.", Category = "Open Telemetry")]
    public Input<bool> StartNewTrace { get; set; } = new(false);

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

    /// <summary>
    /// A step to execute when the child pipeline finishes.
    /// </summary>
    [Port] public IStep? ChildCompleted { get; set; }

    /// <summary>
    /// A step to execute when the child pipeline faults.
    /// </summary>
    [Port] public IStep? ChildFaulted { get; set; }

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        var waitForCompletion = WaitForCompletion.GetOrDefault(context);
        var startNewTrace = StartNewTrace.GetOrDefault(context);
        var items = await context.GetItemSource<object>(Items).ToListAsync(context.CancellationToken).ConfigureAwait(false);
        var count = items.Count;

        // Dispatch the child pipelines.
        foreach (var item in items)
            await DispatchChildPipelineAsync(context, item, waitForCompletion, startNewTrace).ConfigureAwait(false);

        // Store the number of dispatched instances for tracking.
        context.SetProperty(DispatchedInstancesCountKey, count);

        // If we need to wait for the child pipelines to complete (if any), create a bookmark.
        if (waitForCompletion && count > 0)
        {
            var pipelineInstanceId = context.PipelineExecutionContext.Id;
            var bookmarkOptions = new CreateBookmarkArgs
            {
                Callback = OnChildPipelineCompletedAsync,
                Stimulus = new BulkDispatchPipelinesStimulus(pipelineInstanceId)
                {
                    ParentInstanceId = context.PipelineExecutionContext.Id,
                    ScheduledInstanceIdsCount = count
                },
                IncludeStepInstanceId = false,
                AutoBurn = false,
            };

            context.CreateBookmark(bookmarkOptions);
        }
        else
        {
            // Otherwise, we can complete immediately.
            await context.CompleteStepWithOutcomesAsync("Done").ConfigureAwait(false);
        }
    }

    private async ValueTask<string> DispatchChildPipelineAsync(StepExecutionContext context, object item, bool waitForCompletion, bool startNewTrace)
    {
        var pipelineDefinitionId = PipelineDefinitionId.Get(context);
        var pipelineDefinitionService = context.GetRequiredService<IPipelineDefinitionService>();
        var pipelineGraph = await pipelineDefinitionService.FindPipelineGraphAsync(pipelineDefinitionId, VersionOptions.Published).ConfigureAwait(false);

        if (pipelineGraph == null)
            throw new($"No published version of pipeline definition with ID {pipelineDefinitionId} found.");

        var parentInstanceId = context.PipelineExecutionContext.Id;
        var baseInput = Input.GetOrDefault(context);
        var input = baseInput != null ? new Dictionary<string, object>(baseInput) : new Dictionary<string, object>();
        var channelName = ChannelName.GetOrDefault(context);
        var defaultInputItemKey = DefaultItemInputKey.GetOrDefault(context, () => "Item")!;
        var properties = new Dictionary<string, object>
        {
            ["ParentInstanceId"] = parentInstanceId
        };

        if (waitForCompletion) properties["WaitForCompletion"] = true;
        if (startNewTrace) properties["StartNewTrace"] = true;

        var itemDictionary = new Dictionary<string, object>
        {
            [defaultInputItemKey] = item
        };

        var evaluatorOptions = new ExpressionEvaluatorOptions
        {
            Arguments = itemDictionary
        };

        var inputDictionary = item as IDictionary<string, object> ?? itemDictionary;
        input["ParentInstanceId"] = parentInstanceId;
        input.Merge(inputDictionary);

        var pipelineDispatcher = context.GetRequiredService<IPipelineDispatcher>();
        var identityGenerator = context.GetRequiredService<IIdentityGenerator>();
        var evaluator = context.GetRequiredService<IExpressionEvaluator>();
        var correlationId = CorrelationIdFunction != null ? await evaluator.EvaluateAsync<string>(CorrelationIdFunction!, context.ExpressionExecutionContext, evaluatorOptions).ConfigureAwait(false) : null;
        var instanceId = identityGenerator.GenerateId();
        var request = new DispatchPipelineDefinitionRequest(pipelineGraph.Pipeline.Identity.Id)
        {
            ParentPipelineInstanceId = parentInstanceId,
            Input = input,
            Properties = properties,
            CorrelationId = correlationId,
            InstanceId = instanceId
        };
        var options = new DispatchPipelineOptions
        {
            Channel = channelName
        };

        await pipelineDispatcher.DispatchAsync(request, options, context.CancellationToken).ConfigureAwait(false);
        return instanceId;
    }

    private async ValueTask OnChildPipelineCompletedAsync(StepExecutionContext context)
    {
        var input = context.PipelineInput;
        var pipelineInstanceId = input["PipelineInstanceId"].ConvertTo<string>()!;
        var pipelineSubStatus = input["PipelineSubStatus"].ConvertTo<PipelineSubStatus>();
        var finishedInstancesCount = context.GetProperty<long>(CompletedInstancesCountKey) + 1;

        context.SetProperty(CompletedInstancesCountKey, finishedInstancesCount);

        var childInstanceId = new Variable<string>("ChildInstanceId", pipelineInstanceId)
        {
            StorageDriverType = typeof(PipelineInstanceStorageDriver)
        };

        var variables = new List<Variable>
        {
            childInstanceId
        };

        var options = new ScheduleWorkOptions
        {
            Input = input,
            Variables = variables,
            CompletionCallback = OnChildFinishedCompletedAsync
        };

        switch (pipelineSubStatus)
        {
            case PipelineSubStatus.Faulted when ChildFaulted is not null:
                await context.ScheduleStepAsync(ChildFaulted, options).ConfigureAwait(false);
                return;
            case PipelineSubStatus.Finished when ChildCompleted is not null:
                await context.ScheduleStepAsync(ChildCompleted, options).ConfigureAwait(false);
                return;
            default:
                await AttemptToCompleteAsync(context).ConfigureAwait(false);
                break;
        }
    }

    private async ValueTask OnChildFinishedCompletedAsync(StepCompletedContext context)
    {
        await AttemptToCompleteAsync(context.TargetContext).ConfigureAwait(false);
    }

    private async ValueTask AttemptToCompleteAsync(StepExecutionContext context)
    {
        var dispatchedInstancesCount = context.GetProperty<long>(DispatchedInstancesCountKey);
        var finishedInstancesCount = context.GetProperty<long>(CompletedInstancesCountKey);

        if (finishedInstancesCount >= dispatchedInstancesCount)
            await context.CompleteStepWithOutcomesAsync("Completed", "Done").ConfigureAwait(false);
    }
}