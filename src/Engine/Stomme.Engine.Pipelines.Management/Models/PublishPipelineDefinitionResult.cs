namespace Stomme.Engine.Pipelines.Management.Models;

/// <summary>
/// Represents the result of publishing a pipeline definition.
/// </summary>
public record PublishPipelineDefinitionResult(bool Succeeded, ICollection<PipelineValidationError> ValidationErrors, AffectedPipelines AffectedPipelines);