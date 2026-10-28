namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Finds all latest versions of pipeline definitions that reference a specific pipeline definition.
/// </summary>
public interface IPipelineReferenceQuery
{
    /// <summary>
    /// Queries all latest versions of pipeline definitions that reference the specified pipeline definition.
    /// </summary>
    /// <param name="pipelineDefinitionId">The ID of the pipeline definition to query references for.</param>
    /// <param name="cancellationToken">The cancellation token to cancel the operation.</param>
    /// <returns>A collection of pipeline definition IDs that reference the specified pipeline definition.</returns>
    Task<IEnumerable<string>> ExecuteAsync(string pipelineDefinitionId, CancellationToken cancellationToken = default);
}