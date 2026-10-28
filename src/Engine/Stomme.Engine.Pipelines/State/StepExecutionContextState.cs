using Stomme.Engine.Pipelines.Memory;

namespace Stomme.Engine.Pipelines.State;

/// <summary>
/// A serializable shape of <see cref="StepExecutionContext"/>.
/// </summary>
public class StepExecutionContextState
{
    // ReSharper disable once EmptyConstructor
    // Required for JSON serialization configured with reference handling.
    /// <summary>
    /// Constructor.
    /// </summary>
    public StepExecutionContextState()
    {
    }

    /// <summary>
    /// The ID of the step instance.
    /// </summary>
    public string Id { get; set; } = default!;

    /// <summary>
    /// The depth of this step in the call stack (0 for root steps).
    /// </summary>
    public int CallStackDepth { get; set; }
    
    /// <summary>
    /// The ID of the parent of the step instance.
    /// </summary>
    public string? ParentContextId { get; set; }
    
    /// <summary>
    /// The node ID of the scheduled step.
    /// </summary>
    public string ScheduledStepNodeId { get; set; } = default!;
    
    /// <summary>
    /// The node ID of the step that owns the scheduled step.
    /// </summary>
    public string? OwnerStepNodeId { get; set; }
    
    /// <summary>
    /// A bag of properties.
    /// </summary>
    public IDictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
    
    /// <summary>
    /// A bag of metadata.
    /// </summary>
    public IDictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

    /// <summary>
    /// The evaluated values of the step's properties.
    /// </summary>
    public IDictionary<string, object>? StepState { get; set; }
    
    /// <summary>
    /// A list of dynamically created variables.
    /// </summary>
    public ICollection<Variable> DynamicVariables { get; set; } = new List<Variable>();

    /// <summary>
    /// The status of the step.
    /// </summary>
    public StepStatus Status { get; set; }
    
    /// <summary>
    /// Gets or sets a value indicating whether the step is actively executing. 
    /// </summary>
    public bool IsExecuting { get; set; }

    /// <summary>
    /// The number of faults recorded by this step for itself and descendants.
    /// </summary>
    public int FaultCount { get; set; }

    /// <summary>
    /// The time at which the step execution began.
    /// </summary>
    public DateTimeOffset StartedAt { get; set; }
    
    /// <summary>
    /// The time at which the step execution completed.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// An optional tag to associate with the step execution.
    /// </summary>
    public object? Tag { get; set; }
}