using Stomme.Engine.Pipelines.Memory;

namespace Stomme.Engine.Pipelines.Options;

/// <summary>
/// Represents options for scheduling a work item.
/// </summary>
public class ScheduleWorkOptions
{
    /// <summary>The callback to invoke when the work item has completed.</summary>
    public StepCompletionCallback? CompletionCallback { get; set; }

    /// <summary>A tag that can be used to identify the work item.</summary>
    public object? Tag { get; set; }

    /// <summary>A collection of variables to declare in the step execution context that will be created for this work item.</summary>
    public ICollection<Variable>? Variables { get; set; }

    /// <summary>
    /// An existing step execution context to use instead of creating a new one.
    /// </summary>
    public StepExecutionContext? ExistingStepExecutionContext { get; set; }
    
    /// <summary>
    /// A flag indicating whether the work item should be scheduled even if a work item with the same tag already exists.
    /// </summary>
    public bool PreventDuplicateScheduling { get; set; }
    
    /// <summary>
    /// Input to send to the pipeline.
    /// </summary>
    public IDictionary<string, object>? Input { get; set; }

    /// <summary>
    /// The ID of the step execution context that scheduled this work item.
    /// This represents the temporal/execution predecessor that directly triggered execution of this step,
    /// distinct from the structural parent (<see cref="ExistingStepExecutionContext"/> or Owner).
    /// </summary>
    public string? SchedulingStepExecutionId { get; set; }

    /// <summary>
    /// The pipeline instance ID of the pipeline that scheduled this work item.
    /// This is set when crossing pipeline boundaries (e.g., via ExecutePipeline or DispatchPipeline).
    /// For steps within the same pipeline instance, this will be null.
    /// </summary>
    public string? SchedulingPipelineInstanceId { get; set; }
}