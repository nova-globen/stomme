namespace Stomme.Engine.Pipelines.Runtime.Results;

public record CanStartPipelineResult(string? InstanceId, bool CanStart);