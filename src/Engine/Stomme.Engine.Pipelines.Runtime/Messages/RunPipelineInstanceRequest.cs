using Stomme.Engine.Pipelines.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Messages;

/// <summary>
/// A request to run a pipeline instance.
/// </summary>
[UsedImplicitly]
public class RunPipelineInstanceRequest
{
    /// <summary>
    /// The ID of the step that triggered the pipeline instance, if any.
    /// </summary>
    public string? TriggerStepId { get; set; }
    
    /// <summary>
    /// The ID of the bookmark that triggered the pipeline instance, if any.
    /// </summary>
    public string? BookmarkId { get; set; }
    
    /// <summary>
    /// The handle of the step to schedule, if any.
    /// </summary>
    public StepHandle? StepHandle { get; set; }
    
    /// <summary>
    /// Any additional properties to associate with the pipeline instance.
    /// </summary>
    public IDictionary<string, object>? Properties { get; set; }
    
    /// <summary>
    /// The input to the pipeline instance, if any.
    /// </summary>
    public IDictionary<string, object>? Input { get; set; }

    /// <summary>
    /// A collection of variables to be used during the execution of a pipeline instance.
    /// </summary>
    public IDictionary<string, object>? Variables { get; set; }

    /// <summary>
    /// When set to <c>true</c>, include pipeline output in the response.
    /// </summary>
    public bool IncludePipelineOutput { get; set; }

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

    /// <summary>
    /// Represents an empty <see cref="RunPipelineInstanceRequest"/> object used as a default value.
    /// </summary>
    public static RunPipelineInstanceRequest Empty => new();
}