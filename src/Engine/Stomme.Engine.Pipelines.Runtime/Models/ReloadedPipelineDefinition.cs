using Stomme.Engine.Pipelines.Management.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Models;

/// <summary>
/// Represents a reloaded pipeline definition with necessary properties for 
/// identification, versioning, and usability status as a step.
/// </summary>
/// <param name="DefinitionId">The unique identifier for the pipeline definition.</param>
/// <param name="DefinitionVersionId">The unique identifier for the specific version of the pipeline definition.</param>
/// <param name="Version">The version number of the pipeline definition.</param>
/// <param name="UsableAsStep">Indicates whether the pipeline definition can be used as a step.</param>
public record ReloadedPipelineDefinition(string DefinitionId, string DefinitionVersionId, int Version, bool UsableAsStep)
{
    /// <summary>
    /// Creates an instance of <see cref="ReloadedPipelineDefinition"/> from a given <see cref="PipelineDefinition"/>.
    /// </summary>
    /// <param name="pipelineDefinition">The pipeline definition used to create the reloaded pipeline definition.</param>
    /// <returns>A new instance of <see cref="ReloadedPipelineDefinition"/>.</returns>
    public static ReloadedPipelineDefinition FromDefinition(PipelineDefinition pipelineDefinition)
    {
        return new ReloadedPipelineDefinition
        (
            pipelineDefinition.DefinitionId,
            pipelineDefinition.Id,
            pipelineDefinition.Version,
            pipelineDefinition.Options.UsableAsStep ?? false
        );
    }
}