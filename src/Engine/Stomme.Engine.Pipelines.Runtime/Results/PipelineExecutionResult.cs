using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Runtime.Results;

public record PipelineExecutionResult(
    string PipelineInstanceId, 
    PipelineStatus Status, 
    PipelineSubStatus SubStatus, 
    ICollection<Bookmark> Bookmarks, 
    ICollection<StepIncident> Incidents, 
    string? TriggeredStepId,
    IDictionary<string, object> Output);