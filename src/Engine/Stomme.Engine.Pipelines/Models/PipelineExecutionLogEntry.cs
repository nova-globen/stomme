namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// Represents a pipeline execution log entry.
/// </summary>
public record PipelineExecutionLogEntry(
    string StepInstanceId,
    string? ParentStepInstanceId,
    string StepId,
    string StepType,
    int StepTypeVersion,
    string? StepName,
    string NodeId,
    [property: Obsolete("Use StepInstanceId instead to locate step state from StepExecutionLog")]IDictionary<string, object>? StepState,
    DateTimeOffset Timestamp,
    long Sequence,
    string? EventName,
    string? Message,
    string? Source,
    object? Payload);