using Stomme.Engine.Pipelines.Memory;

namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// Represents a work item that can be scheduled for execution.
/// </summary>
public class StepWorkItem
{
    /// <summary>
    /// Creates a new instance of the <see cref="StepWorkItem"/> class.
    /// </summary>
    public StepWorkItem(
        IStep step,
        StepExecutionContext? owner = null,
        object? tag = null,
        IEnumerable<Variable>? variables = null,
        StepExecutionContext? existingStepExecutionContext = null,
        IDictionary<string, object>? input = null,
        string? schedulingStepExecutionId = null,
        string? schedulingPipelineInstanceId = null,
        int? schedulingCallStackDepth = null)
    {
        Step = step;
        Owner = owner;
        Tag = tag;
        Variables = variables;
        ExistingStepExecutionContext = existingStepExecutionContext;
        Input = input ?? new Dictionary<string, object>();
        SchedulingStepExecutionId = schedulingStepExecutionId;
        SchedulingPipelineInstanceId = schedulingPipelineInstanceId;
        SchedulingCallStackDepth = schedulingCallStackDepth;
    }

    /// <summary>
    /// The step to be executed.
    /// </summary>
    public IStep Step { get; }

    /// <summary>
    /// The step execution context that owns this work item, if any.
    /// </summary>
    public StepExecutionContext? Owner { get; set; }

    /// <summary>
    /// A tag that can be used to identify the work item.
    /// </summary>
    public object? Tag { get; set; }

    /// <summary>
    /// A set of variables to be created as part of the step execution context.
    /// </summary>
    public IEnumerable<Variable>? Variables { get; set; }

    /// <summary>
    /// An existing step execution context to schedule, if any.
    /// </summary>
    public StepExecutionContext? ExistingStepExecutionContext { get; set; }

    /// <summary>
    /// Optional input to pass to the step.
    /// </summary>
    public IDictionary<string, object> Input { get; set; }

    /// <summary>
    /// The ID of the step execution context that scheduled this work item.
    /// This represents the temporal/execution predecessor that directly triggered execution of this step,
    /// distinct from the structural parent (<see cref="Owner"/>).
    /// </summary>
    public string? SchedulingStepExecutionId { get; set; }

    /// <summary>
    /// The pipeline instance ID of the pipeline that scheduled this work item.
    /// This is set when crossing pipeline boundaries (e.g., via ExecutePipeline or DispatchPipeline).
    /// For steps within the same pipeline instance, this will be null.
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