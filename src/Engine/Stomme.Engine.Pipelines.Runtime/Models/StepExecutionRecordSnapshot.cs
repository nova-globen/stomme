namespace Stomme.Engine.Pipelines.Runtime;

public class StepExecutionRecordSnapshot
{
    public string Id { get; set; } = null!;
    public string? TenantId { get; set; }
    public string PipelineInstanceId { get; set; } = null!;
    public string StepId { get; set; } = null!;
    public string StepNodeId { get; set; } = null!;
    public string StepType { get; set; } = null!;
    public int StepTypeVersion { get; set; }
    public string? StepName { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public bool HasBookmarks { get; set; }
    public StepStatus Status { get; set; }
    public int AggregateFaultCount { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? SerializedStepState { get; set; }
    public string? SerializedOutputs { get; set; }
    public string? SerializedProperties { get; set; }
    public string? SerializedPayload { get; set; }
    public string? SerializedMetadata { get; set; }
    public string? SerializedException { get; set; }
    public string? SerializedStepStateCompressionAlgorithm { get; set; }
}