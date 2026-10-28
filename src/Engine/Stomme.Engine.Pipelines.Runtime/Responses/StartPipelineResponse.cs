using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime.Messages;

namespace Stomme.Engine.Pipelines.Runtime;

public record StartPipelineResponse
{
    /// <summary>
    /// Indicates whether the pipeline instance can be started.
    /// </summary>
    public bool CannotStart { get; set; }
    
    /// <summary>
    /// The ID of the pipeline instance.
    /// </summary>
    public string? PipelineInstanceId { get; set; } 
    
    /// <summary>
    /// The status of the pipeline instance.
    /// </summary>
    public PipelineStatus? Status { get; set; }

    /// <summary>
    /// The sub-status of the pipeline instance.
    /// </summary>
    public PipelineSubStatus? SubStatus { get; set; }
    
    public ICollection<Bookmark> Bookmarks { get; set; } = new List<Bookmark>();

    /// <summary>
    /// Any incidents that occurred during the execution of the pipeline instance.
    /// </summary>
    public ICollection<StepIncident> Incidents { get; set; } = new List<StepIncident>();
    
    public RunPipelineInstanceResponse ToRunPipelineInstanceResponse() => new()
    {
        PipelineInstanceId = PipelineInstanceId!,
        Status = Status!.Value,
        SubStatus = SubStatus!.Value,
        Incidents = Incidents
    };
}