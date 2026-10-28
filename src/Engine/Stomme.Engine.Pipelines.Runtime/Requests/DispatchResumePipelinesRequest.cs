namespace Stomme.Engine.Pipelines.Runtime.Requests;

public record DispatchResumePipelinesRequest(
    string StepTypeName, 
    object BookmarkPayload, 
    string? CorrelationId = null,
    string? PipelineInstanceId = null,
    string? StepInstanceId = null,
    IDictionary<string, object>? Input = null);