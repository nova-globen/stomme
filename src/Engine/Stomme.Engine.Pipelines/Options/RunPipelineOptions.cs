using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Options;

/// <summary>
/// Provides options for running a pipeline.
/// </summary>
public class RunPipelineOptions
{
    public string? PipelineInstanceId { get; set; }
    public string? CorrelationId { get; set; }
    public string? BookmarkId { get; set; }
    public StepHandle? StepHandle { get; set; }
    public IDictionary<string, object>? Input { get; set; }
    public IDictionary<string, object>? Variables { get; set; }
    public IDictionary<string, object>? Properties { get; set; }
    public string? TriggerStepId { get; set; }
    public string? ParentPipelineInstanceId { get; set; }

    /// <summary>
    /// The ID of the step execution context that scheduled this pipeline execution (for cross-pipeline call stack tracking).
    /// This is set when a parent pipeline invokes this pipeline via ExecutePipeline or DispatchPipeline.
    /// </summary>
    public string? SchedulingStepExecutionId { get; set; }

    /// <summary>
    /// The pipeline instance ID of the parent pipeline that scheduled this pipeline execution.
    /// This is set when crossing pipeline boundaries.
    /// </summary>
    public string? SchedulingPipelineInstanceId { get; set; }

    /// <summary>
    /// The call stack depth of the scheduling step execution context.
    /// This is used to calculate the call stack depth when the scheduling context is not present
    /// in StepExecutionContexts (e.g., for cross-pipeline invocations).
    /// Should be set to the depth of the scheduling step (not depth + 1, as the increment is applied automatically).
    /// </summary>
    public int? SchedulingCallStackDepth { get; set; }
}