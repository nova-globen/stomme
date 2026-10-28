using Stomme.Engine.Pipelines.Management.Models;

namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Builds a complete graph of pipeline references by recursively discovering all pipelines
/// that consume (directly or indirectly) a given pipeline definition.
/// </summary>
public interface IPipelineReferenceGraphBuilder
{
    /// <summary>
    /// Builds a complete reference graph starting from the specified pipeline definition.
    /// </summary>
    /// <param name="definitionId">The ID of the pipeline definition to start from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A <see cref="PipelineReferenceGraph"/> containing all reference relationships.</returns>
    Task<PipelineReferenceGraph> BuildGraphAsync(string definitionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds a complete reference graph starting from multiple pipeline definitions.
    /// </summary>
    /// <param name="definitionIds">The IDs of the pipeline definitions to start from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A merged <see cref="PipelineReferenceGraph"/> containing all reference relationships from all starting points.</returns>
    Task<PipelineReferenceGraph> BuildGraphAsync(IEnumerable<string> definitionIds, CancellationToken cancellationToken = default);
}

