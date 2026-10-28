namespace Stomme.Engine.Pipelines;

/// <summary>
/// Represents the status of a step.
/// </summary>
public enum StepStatus
{
    /// <summary>
    /// The step is in the Pending state.
    /// </summary>
    Pending,
    
    /// <summary>
    /// The step is in the Running state. While in this state, the step is not necessarily being actively executed. This state represents a logical status rather than a physical action.
    /// </summary>
    Running,
    
    /// <summary>
    /// The step is in the Completed state.
    /// </summary>
    Completed,
    
    /// <summary>
    /// The step is in the Canceled state.
    /// </summary>
    Canceled,
    
    /// <summary>
    /// The step is in the Faulted state.
    /// </summary>
    Faulted
}