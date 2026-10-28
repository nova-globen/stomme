namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents the result of executing a pipeline.
/// </summary>
public class ExecutePipelineResult
{
    public string PipelineDefinitionVersionId { get; set; } = default!;
    public string PipelineInstanceId { get; set; } = default!;
    public string? CorrelationId { get; set; }
    public PipelineStatus Status { get; set; }
    public PipelineSubStatus SubStatus { get; set; }
    public IDictionary<string, object>? Output { get; set; }
}