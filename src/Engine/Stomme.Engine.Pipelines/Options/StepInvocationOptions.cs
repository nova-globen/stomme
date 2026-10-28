using Stomme.Engine.Pipelines.Memory;

namespace Stomme.Engine.Pipelines.Options;

/// <summary>
/// Represents options for invoking a step.
/// </summary>
public class StepInvocationOptions
{
    /// <summary>Initializes a new instance of the <see cref="StepInvocationOptions"/> class.</summary>
    public StepInvocationOptions()
    {
        Input = new Dictionary<string, object>();
    }

    /// <summary>Initializes a new instance of the <see cref="StepInvocationOptions"/> class.</summary>
    /// <param name="owner">The step execution context that owns this invocation.</param>
    /// <param name="tag">An optional tag that can be used to identify the invocation.</param>
    /// <param name="variables">The variables to declare in the step execution context that will be created for this invocation.</param>
    /// <param name="existingStepExecutionContext">An existing step execution context to reuse.</param>
    /// <param name="input">Optional input to pass to the step.</param>
    public StepInvocationOptions(
        StepExecutionContext? owner,
        object? tag,
        IEnumerable<Variable>? variables,
        StepExecutionContext? existingStepExecutionContext = default,
        IDictionary<string, object>? input = default)
    {
        Owner = owner;
        Tag = tag;
        Variables = variables;
        ExistingStepExecutionContext = existingStepExecutionContext;
        Input = input ?? new Dictionary<string, object>();
    }

    /// <summary>The step execution context that owns this invocation.</summary>
    public StepExecutionContext? Owner { get; set; }

    /// <summary>An optional tag that can be used to identify the invocation.</summary>
    public object? Tag { get; set; }

    /// <summary>The variables to declare in the step execution context that will be created for this invocation.</summary>
    public IEnumerable<Variable>? Variables { get; set; }

    /// <summary>An existing step execution context to reuse.</summary>
    public StepExecutionContext? ExistingStepExecutionContext { get; set; }

    /// <summary>
    /// Optional input to pass to the step.
    /// </summary>
    public IDictionary<string, object> Input { get; set; }

    /// <summary>
    /// The ID of the step execution context that scheduled this invocation.
    /// This represents the temporal/execution predecessor that directly triggered execution of this step,
    /// distinct from the structural parent (<see cref="Owner"/>).
    /// </summary>
    public string? SchedulingStepExecutionId { get; set; }

    /// <summary>
    /// The pipeline instance ID of the pipeline that scheduled this invocation.
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