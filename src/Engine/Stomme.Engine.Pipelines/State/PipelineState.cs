using System.ComponentModel.DataAnnotations.Schema;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.State;

/// <summary>
/// Represents the current state of a pipeline. 
/// </summary>
public class PipelineState
{
    /// <summary>
    /// Gets or sets the ID.
    /// </summary>
    public string Id { get; set; } = null!;

    /// <summary>
    /// The pipeline definition ID.
    /// </summary>
    public string DefinitionId { get; set; } = null!;

    /// <summary>
    /// The pipeline definition version ID.
    /// </summary>
    public string DefinitionVersionId { get; set; } = null!;

    /// <summary>
    /// The pipeline definition version.
    /// </summary>
    public int DefinitionVersion { get; set; }

    /// <summary>
    /// The ID of the parent pipeline.
    /// </summary>
    public string? ParentPipelineInstanceId { get; set; }

    /// <summary>
    /// The correlation ID of the pipeline, if any.
    /// </summary>
    public string? CorrelationId { get; set; }
    
    /// <summary>
    /// Gets or sets the name of the pipeline instance.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// The status of the pipeline.
    /// </summary>
    public PipelineStatus Status { get; set; }

    /// <summary>
    /// The sub status of the pipeline.
    /// </summary>
    public PipelineSubStatus SubStatus { get; set; }
    
    /// <summary>
    /// Gets or sets a value indicating whether the pipeline instance is actively executing. 
    /// </summary>
    public bool IsExecuting { get; set; }
    
    /// <summary>
    /// Collected bookmarks.
    /// </summary>
    [NotMapped]
    public ICollection<Bookmark> Bookmarks { get; set; } = new List<Bookmark>();

    /// <summary>
    /// A collection of incidents that may have occurred during execution.
    /// </summary>
    public ICollection<StepIncident> Incidents { get; set; } = new List<StepIncident>();

    /// <summary>
    /// Gets or sets the value indicating whether the pipeline is a system pipeline.
    /// </summary>
    public bool IsSystem { get; set; }

    /// <summary>
    /// A list of callbacks that steps registered in order to be notified when the steps they scheduled complete. 
    /// </summary>
    public ICollection<CompletionCallbackState> CompletionCallbacks { get; set; } = new List<CompletionCallbackState>();

    /// <summary>
    /// A flattened list of <see cref="StepExecutionContextState"/> objects, representing the various active "call stacks" of the pipeline.
    /// </summary>
    [NotMapped]
    public ICollection<StepExecutionContextState> StepExecutionContexts { get; set; } = new List<StepExecutionContextState>();

    /// <summary>
    /// A list of scheduled steps.
    /// </summary>
    public ICollection<StepWorkItemState> ScheduledSteps { get; set; } = new List<StepWorkItemState>();

    /// <summary>
    /// The current execution log sequence number.
    /// </summary>
    public long ExecutionLogSequence { get; set; }

    /// <summary>
    /// A dictionary of inputs sent to the pipeline.
    /// </summary>
    public IDictionary<string, object> Input { get; set; } = new Dictionary<string, object>();

    /// <summary>
    /// A dictionary of outputs produced by the pipeline.
    /// </summary>
    public IDictionary<string, object> Output { get; set; } = new Dictionary<string, object>();

    /// <summary>
    /// A global property bag that contains properties set by application code and/or steps.
    /// </summary>
    public IDictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();

    /// <summary>
    /// The created time of the pipeline.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The last updated time of the pipeline.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// The finished time of the pipeline.
    /// </summary>
    public DateTimeOffset? FinishedAt { get; set; }
}