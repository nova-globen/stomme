using Stomme.Engine.Pipelines.Management.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Matches;

public record PipelineMatch(string? CorrelationId, object? Payload);