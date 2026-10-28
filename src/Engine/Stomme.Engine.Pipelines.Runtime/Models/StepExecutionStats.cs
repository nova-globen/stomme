namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Execution stats about a given step.
/// </summary>
public class StepExecutionStats
{
    /// <summary>
    /// Gets or sets the ID of the step.
    /// </summary>
    public string StepId { get; set; } = null!;
    
    /// <summary>
    /// Gets or sets the node ID of the step.
    /// </summary>
    public string StepNodeId { get; set; } = null!;
    
    /// <summary>
    /// Gets or sets the number of uncompleted executions.
    /// </summary>
    public long StartedCount { get; set; }
    
    /// <summary>
    /// Gets or sets the number of completed executions.
    /// </summary>
    public long CompletedCount { get; set; }
    
    /// <summary>
    /// Gets or sets the number of uncompleted executions.
    /// </summary>
    public long UncompletedCount { get; set; }

    /// <summary>
    /// Gets or sets a value whether this step is waiting for bookmarks to be resumed.
    /// </summary>
    public bool IsBlocked { get; set; }

    /// <summary>
    /// Gets or sets a value whether this step is faulted.
    /// </summary>
    public bool IsFaulted { get; set; }
    
    /// <summary>
    /// Gets or sets the total count of faults aggregated from the step execution and its descendants.
    /// </summary>
    public int AggregateFaultCount { get; set; }

    /// <summary>
    /// Gets or sets metadata related to the step execution.
    /// </summary>
    public IDictionary<string, object>? Metadata { get; set; }
}