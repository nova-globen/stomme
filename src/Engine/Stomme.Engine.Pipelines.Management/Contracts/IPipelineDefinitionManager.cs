using Stomme.Engine.Pipelines.Management.Entities;

namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Provides operations for managing pipeline definitions.
/// </summary>
public interface IPipelineDefinitionManager
{
    /// <summary>
    /// Deletes all pipeline definition versions with the specified definition ID.
    /// </summary>
    /// <param name="definitionId">The definition ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of deleted pipeline definitions.</returns>
    Task<long> DeleteByDefinitionIdAsync(string definitionId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Deletes a specific version of a pipeline definition.
    /// </summary>
    /// <param name="id">The ID of the pipeline definition version.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the pipeline definition version was deleted, otherwise false.</returns>
    Task<bool> DeleteByIdAsync(string id, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Deletes a specific pipeline definition version.
    /// </summary>
    /// <param name="definitionId">The ID of the pipeline definition.</param>
    /// <param name="version">The version number of the pipeline definition.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True if the pipeline definition version was deleted, otherwise false.</returns>
    Task<bool> DeleteVersionAsync(string definitionId, int version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a specific pipeline definition version.
    /// </summary>
    /// <param name="definitionToDelete"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<bool> DeleteVersionAsync(PipelineDefinition definitionToDelete, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes all pipeline definition versions with the specified definition IDs.
    /// </summary>
    /// <param name="definitionIds">The definition IDs.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of deleted pipeline definitions.</returns>
    Task<long> BulkDeleteByDefinitionIdsAsync(IEnumerable<string> definitionIds, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Deletes all pipeline definition versions with the specified IDs.
    /// </summary>
    /// <param name="ids">The IDs.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of deleted pipeline definitions.</returns>
    Task<long> BulkDeleteByIdsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Creates a new pipeline definition from the specified version.
    /// </summary>
    /// <param name="definitionId">The definition ID.</param>
    /// <param name="version">The version number.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new pipeline definition.</returns>
    Task<PipelineDefinition> RevertVersionAsync(string definitionId, int version, CancellationToken cancellationToken = default);
}