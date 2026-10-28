using Stomme.Engine.Pipelines.Memory;

namespace Stomme.Engine.Pipelines.State;

/// <summary>
/// A serialized representation of a step work item.
/// </summary>
public class StepWorkItemState
{
    /// <summary>
    /// The ID of the step to be executed.
    /// </summary>
    public string StepNodeId { get; set; } = default!;
    
    /// <summary>
    /// The ID of the parent step execution context, if any.
    /// </summary>
    public string? OwnerContextId { get; set; }
    
    /// <summary>
    /// A tag that can be used to identify the work item.
    /// </summary>
    public object? Tag { get; set; }
    
    /// <summary>
    /// A set of variables to be created as part of the step execution context, if any.
    /// </summary>
    public ICollection<Variable>? Variables { get; set; }
    
    /// <summary>
    /// The ID of an existing step execution context to schedule, if any.
    /// </summary>
    public string? ExistingStepExecutionContextId { get; set; }

    /// <summary>
    /// Optional input to pass to the step.
    /// </summary>
    public IDictionary<string, object> Input { get; set; } = new Dictionary<string, object>();
}