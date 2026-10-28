using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Models;

namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Updates references to the specified pipeline of all pipelines that reference it.
/// </summary>
public interface IPipelineReferenceUpdater
{
    /// <summary>
    /// Updates references to the specified pipeline of all pipelines that reference it.
    /// </summary>
    /// <param name="referencedDefinition">The pipeline definition that is being referenced. All pipelines that reference this definition will be updated to use this newest version.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the operation.</returns>
    Task<UpdatePipelineReferencesResult> UpdatePipelineReferencesAsync(PipelineDefinition referencedDefinition, CancellationToken cancellationToken = default);
}