namespace Stomme.Engine.Pipelines.Management.Contracts;

/// <summary>
/// Represents a service for updating the step registry.
/// </summary>
public interface IPipelineDefinitionStepRegistryUpdater
{
    /// <summary>
    /// Tries to add a pipeline as a step to the registry.
    /// </summary>
    /// <param name="pipelineDefinitionVersionId">The version ID of the pipeline definition.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    Task AddToRegistry(string pipelineDefinitionVersionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes pipeline definition steps from the <see cref="IStepRegistry"/>.
    /// </summary>
    /// <param name="pipelineDefinitionId">The ID of the pipeline definition to remove.</param>
    void RemoveDefinitionFromRegistry(string pipelineDefinitionId);
    
    /// <summary>
    /// Removes a pipeline definition version step from the <see cref="IStepRegistry"/>.
    /// </summary>
    /// <param name="pipelineDefinitionVersionId">The ID of the pipeline definition to remove.</param>
    void RemoveDefinitionVersionFromRegistry(string pipelineDefinitionVersionId);
}