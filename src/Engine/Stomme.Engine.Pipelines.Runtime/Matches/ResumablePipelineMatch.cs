using Stomme.Engine.Pipelines.Management.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Matches;

public record ResumablePipelineMatch(string PipelineInstanceId, string? CorrelationId, string? BookmarkId, object? Payload)
    : PipelineMatch(CorrelationId, Payload);