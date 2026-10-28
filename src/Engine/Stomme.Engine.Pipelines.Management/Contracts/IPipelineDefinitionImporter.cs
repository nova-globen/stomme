using Stomme.Engine.Pipelines.Management.Models;

namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Imports a pipeline definition.
/// </summary>
public interface IPipelineDefinitionImporter
{
    /// <summary>
    /// Imports a pipeline definition.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The imported pipeline definition.</returns>
    Task<ImportPipelineResult> ImportAsync(SavePipelineDefinitionRequest request, CancellationToken cancellationToken = default);
}