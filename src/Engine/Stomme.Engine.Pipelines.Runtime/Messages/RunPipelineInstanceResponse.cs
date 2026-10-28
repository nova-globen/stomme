using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Runtime.Messages;

/// <summary>
/// Represents a response from running a pipeline instance.
/// </summary>
public record RunPipelineInstanceResponse
{
    /// <summary>
    /// The ID of the pipeline instance.
    /// </summary>
    public string PipelineInstanceId { get; set; } = null!; 
    
    /// <summary>
    /// The status of the pipeline instance.
    /// </summary>
    public PipelineStatus Status { get; set; }

    /// <summary>
    /// The sub-status of the pipeline instance.
    /// </summary>
    public PipelineSubStatus SubStatus { get; set; }

    public ICollection<Bookmark> Bookmarks { get; set; } = new List<Bookmark>();

    /// <summary>
    /// Any incidents that occurred during the execution of the pipeline instance.
    /// </summary>
    public ICollection<StepIncident> Incidents { get; set; } = new List<StepIncident>();

    /// <summary>
    /// The output produced by the pipeline instance.
    /// </summary>
    public IDictionary<string, object>? Output { get; set; }
}