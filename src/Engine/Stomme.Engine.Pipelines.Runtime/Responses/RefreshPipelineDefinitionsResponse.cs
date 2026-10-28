namespace Stomme.Engine.Pipelines.Runtime.Responses;

/// <summary>
/// Represents a response to a request to refresh pipeline definitions.
/// </summary>
public record RefreshPipelineDefinitionsResponse(
    ICollection<string> Refreshed,
    ICollection<string> NotFound,
    RefreshPipelineDefinitionsStatus Status = RefreshPipelineDefinitionsStatus.Completed
    );