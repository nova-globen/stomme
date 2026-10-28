using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Models;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Publishes pipeline definitions.
/// </summary>
[PublicAPI]
public interface IPipelineDefinitionPublisher
{
    /// <summary>
    /// Creates a new pipeline definition.
    /// </summary>
    /// <param name="root">Optionally provide the root step. If not specified, <see cref="Sequence" /> will be used/></param>
    /// <returns>The new pipeline definition.</returns>
    [Obsolete( "Use NewAsync instead.", error: false)]
    PipelineDefinition New(IStep? root = null);
    
    /// <summary>
    /// Creates a new pipeline definition.
    /// </summary>
    /// <param name="root">Optionally provide the root step. If not specified, <see cref="Sequence" /> will be used/></param>
    /// <param name="cancellationToken">A cancellation token</param>
    /// <returns>The new pipeline definition.</returns>
    Task<PipelineDefinition> NewAsync(IStep? root = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a pipeline definition.
    /// </summary>
    /// <param name="definitionId">The ID of the pipeline definition to publish.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The published pipeline definition.</returns>
    Task<PublishPipelineDefinitionResult> PublishAsync(string definitionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a pipeline definition.
    /// </summary>
    /// <param name="definition">The pipeline definition to publish.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The published pipeline definition.</returns>
    Task<PublishPipelineDefinitionResult> PublishAsync(PipelineDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retracts a pipeline definition.
    /// </summary>
    /// <param name="definitionId">The ID of the pipeline definition to retract.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The retracted pipeline definition.</returns>
    Task<PipelineDefinition?> RetractAsync(string definitionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retracts a pipeline definition.
    /// </summary>
    /// <param name="definition">The pipeline definition to retract.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The retracted pipeline definition.</returns>
    Task<PipelineDefinition> RetractAsync(PipelineDefinition definition, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Creates a new pipeline definition from the specified version.
    /// </summary>
    /// <param name="definitionId">The definition ID.</param>
    /// <param name="version">The version number.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new pipeline definition.</returns>
    Task<PipelineDefinition> RevertVersionAsync(string definitionId, int version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets or creates a draft for the specified pipeline definition.
    /// </summary>
    /// <param name="definitionId">The ID of the pipeline definition.</param>
    /// <param name="versionOptions">The version options.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The draft pipeline definition.</returns>
    Task<PipelineDefinition?> GetDraftAsync(string definitionId, VersionOptions versionOptions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a draft for the specified pipeline definition.
    /// </summary>
    /// <param name="definition">The pipeline definition.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The saved pipeline definition.</returns>
    Task<PipelineDefinition> SaveDraftAsync(PipelineDefinition definition, CancellationToken cancellationToken = default);
}