namespace Stomme.Engine.Pipelines.Runtime.Matches;

public record StartablePipelineMatch(string? CorrelationId, string? StepId, string? DefinitionId, object? Payload)
    : PipelineMatch(CorrelationId, Payload);