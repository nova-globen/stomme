using System.Runtime.CompilerServices;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.Runtime.Stimuli;
using Stomme.Engine.Pipelines.UIHints;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Steps;

/// <summary>
/// Creates a new pipeline instance of the specified pipeline and dispatches it for execution.
/// </summary>
[Step("Stomme", "Composition", "Create a new pipeline instance of the specified pipeline and execute it.", Kind = StepKind.Task)]
[UsedImplicitly]
public class ExecutePipeline : Step<ExecutePipelineResult>
{
    /// <inheritdoc />
    public ExecutePipeline([CallerFilePath] string? source = null, [CallerLineNumber] int? line = null) : base(source, line)
    {
    }

    /// <summary>
    /// The definition ID of the pipeline to execute. 
    /// </summary>
    [Input(
        DisplayName = "Pipeline Definition",
        Description = "The definition ID of the pipeline to execute.",
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
    /// True to wait for the child pipeline to complete before completing this step. If not set, the child pipeline will be executed until it either completes or goes idle before this step completes.
    /// </summary>
    [Input(Description = "Wait for the child pipeline to complete before completing this step.")]
    public Input<bool> WaitForCompletion { get; set; } = null!;

    /// <summary>
    /// Stomme hand fix: go on when the called pipeline fails or is stopped - its result says how it
    /// finished. Unticked, this step fails with the called pipeline's reason, as n8n's Execute Workflow does.
    /// </summary>
    [Input(DisplayName = "Go on when it fails", Description = "Go on when the called pipeline fails or is stopped - its result says how it finished. Unticked, this step fails with the called pipeline's reason.")]
    public Input<bool>? GoOnWhenItFails { get; set; }

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(StepExecutionContext context)
    {
        var waitForCompletion = WaitForCompletion.Get(context);
        var (result, reason) = await ExecutePipelineAsync(context, waitForCompletion).ConfigureAwait(false);

        if (!waitForCompletion || result.Status == PipelineStatus.Finished)
        {
            FailWhenItFailed(context, result, reason);
            context.SetResult(result);
            await context.CompleteStepAsync().ConfigureAwait(false);
            return;
        }

        // Since the child pipeline is still running, we need to wait for it to complete using a bookmark.
        var bookmarkOptions = new CreateBookmarkArgs
        {
            Callback = OnChildPipelineCompletedAsync,
            Stimulus = new ExecutePipelineStimulus(result.PipelineInstanceId),
            IncludeStepInstanceId = false
        };
        context.CreateBookmark(bookmarkOptions);
    }

    private async ValueTask<(ExecutePipelineResult Result, string? Reason)> ExecutePipelineAsync(StepExecutionContext context, bool waitForCompletion)
    {
        var pipelineDefinitionId = PipelineDefinitionId.Get(context);
        var pipelineDefinitionService = context.GetRequiredService<IPipelineDefinitionService>();
        var pipelineGraph = await pipelineDefinitionService.FindPipelineGraphAsync(pipelineDefinitionId, VersionOptions.Published, context.CancellationToken).ConfigureAwait(false);

        if (pipelineGraph == null)
            throw new($"No published version of pipeline definition with ID {pipelineDefinitionId} found.");

        var parentInstanceId = context.PipelineExecutionContext.Id;
        var input = Input.GetOrDefault(context) ?? new Dictionary<string, object>();
        var correlationId = CorrelationId.GetOrDefault(context);
        var pipelineInvoker = context.GetRequiredService<IPipelineInvoker>();
        var identityGenerator = context.GetRequiredService<IIdentityGenerator>();
        var properties = new Dictionary<string, object>
        {
            ["ParentInstanceId"] = parentInstanceId
        };

        // If we need to wait for the child pipeline to complete, set the property. This will be used by the ResumeExecutePipelineStep to resume the parent pipeline.
        if (waitForCompletion)
            properties["WaitForCompletion"] = true;

        input["ParentInstanceId"] = parentInstanceId;

        var options = new RunPipelineOptions
        {
            ParentPipelineInstanceId = parentInstanceId,
            Input = input,
            Properties = properties,
            CorrelationId = correlationId,
            PipelineInstanceId = identityGenerator.GenerateId(),
            SchedulingStepExecutionId = context.Id,
            SchedulingPipelineInstanceId = parentInstanceId,
            SchedulingCallStackDepth = context.CallStackDepth
        };

        var pipelineResult = await pipelineInvoker.InvokeAsync(pipelineGraph, options, context.CancellationToken).ConfigureAwait(false);
        var info = new ExecutePipelineResult
        {
            PipelineInstanceId = options.PipelineInstanceId,
            Status = pipelineResult.PipelineState.Status,
            SubStatus = pipelineResult.PipelineState.SubStatus,
            Output = pipelineResult.PipelineState.Output
        };

        return (info, pipelineResult.PipelineState.Incidents.FirstOrDefault()?.Message);
    }

    private async ValueTask OnChildPipelineCompletedAsync(StepExecutionContext context)
    {
        // Stomme hand fix: the result an immediate finish gives, read from the child's (ChildPipelineCompletion).
        var result = ChildPipelineCompletion.ResultOf(context.PipelineInput);
        FailWhenItFailed(context, result, ChildPipelineCompletion.ReasonOf(context.PipelineInput));
        context.Set(Result, result);
        await context.CompleteStepAsync().ConfigureAwait(false);
    }

    // Stomme hand fix: a called pipeline that failed, or was stopped, fails this step with its reason - the
    // run then fails here, or takes this step's Failed path - unless the step goes on when it fails. Upstream went on regardless,
    // with no output to go on with.
    private void FailWhenItFailed(StepExecutionContext context, ExecutePipelineResult result, string? reason)
    {
        if (result.Status != PipelineStatus.Finished || result.SubStatus == PipelineSubStatus.Finished || GoOnWhenItFails?.GetOrDefault(context) == true)
            return;

        throw new FaultException("CALLED_PIPELINE_FAILED", "Pipelines", "Business", result.SubStatus == PipelineSubStatus.Cancelled
            ? "The called pipeline's run was stopped."
            : "The called pipeline failed" + (string.IsNullOrWhiteSpace(reason) ? "." : ": " + reason));
    }
}