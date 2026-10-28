using Stomme.Engine.Pipelines.Management.Entities;

namespace Stomme.Engine.Pipelines.Management.Models;

/// <summary>
/// Represents the result of importing a pipeline definition.
/// </summary>
/// <param name="Succeeded">True if the import was successful, otherwise false.</param>
/// <param name="PipelineDefinition">The imported pipeline definition.</param>
/// <param name="ValidationErrors">Any validation errors that occurred during the import.</param>
public record ImportPipelineResult(bool Succeeded, PipelineDefinition PipelineDefinition, ICollection<PipelineValidationError> ValidationErrors);