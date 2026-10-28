using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Engine.Pipelines.Management.Models;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// Manages materialization of <see cref="PipelineDefinition"/> to <see cref="Pipeline"/> objects. 
/// </summary>
public interface IPipelineDefinitionService
{
    /// <summary>
    /// Constructs an executable <see cref="Pipeline"/> from the specified <see cref="PipelineDefinition"/>.
    /// </summary>
    Task<PipelineGraph> MaterializePipelineAsync(PipelineDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks for a <see cref="PipelineDefinition"/> by the specified definition ID and <see cref="VersionOptions"/>.
    /// </summary>
    Task<PipelineDefinition?> FindPipelineDefinitionAsync(string definitionId, VersionOptions versionOptions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks for a <see cref="PipelineDefinition"/> by the specified version ID.
    /// </summary>
    Task<PipelineDefinition?> FindPipelineDefinitionAsync(string definitionVersionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks for a <see cref="PipelineDefinition"/> by the specified <see cref="PipelineDefinitionHandle"/>.
    /// </summary>
    Task<PipelineDefinition?> FindPipelineDefinitionAsync(PipelineDefinitionHandle handle, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Looks for a <see cref="PipelineDefinition"/> by the specified <see cref="PipelineDefinitionFilter"/>.
    /// </summary>
    Task<PipelineDefinition?> FindPipelineDefinitionAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks for a <see cref="PipelineGraph"/> by the specified definition ID and <see cref="VersionOptions"/>.
    /// </summary>
    Task<PipelineGraph?> FindPipelineGraphAsync(string definitionId, VersionOptions versionOptions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks for a <see cref="PipelineGraph"/> by the specified version ID.
    /// </summary>
    Task<PipelineGraph?> FindPipelineGraphAsync(string definitionVersionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks for a <see cref="PipelineGraph"/> by the specified <see cref="PipelineDefinitionHandle"/>.
    /// </summary>
    Task<PipelineGraph?> FindPipelineGraphAsync(PipelineDefinitionHandle definitionHandle, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Looks for a <see cref="PipelineGraph"/> by the specified <see cref="PipelineDefinitionFilter"/>.
    /// </summary>
    Task<PipelineGraph?> FindPipelineGraphAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Looks for all <see cref="PipelineGraph"/>s that match the specified <see cref="PipelineDefinitionFilter"/>.
    /// </summary>
    Task<IEnumerable<PipelineGraph>> FindPipelineGraphsAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to retrieve a <see cref="PipelineGraph"/> and its corresponding <see cref="PipelineDefinition"/>
    /// based on the specified pipeline definition ID and version options.
    /// </summary>
    /// <param name="definitionId">The ID of the pipeline definition to search for.</param>
    /// <param name="versionOptions">The versioning options that determine which version of the pipeline definition to search for.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A <see cref="PipelineGraphFindResult"/> containing the pipeline graph and its associated definition,
    /// or indicating that they do not exist.
    /// </returns>
    Task<PipelineGraphFindResult> TryFindPipelineGraphAsync(string definitionId, VersionOptions versionOptions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to locate a <see cref="PipelineGraph"/> based on the specified pipeline definition version ID.
    /// </summary>
    /// <param name="definitionVersionId">The unique identifier of the pipeline definition version.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A <see cref="PipelineGraphFindResult"/> containing the pipeline graph and its associated definition,
    /// or indicating that they do not exist.
    /// </returns>
    Task<PipelineGraphFindResult> TryFindPipelineGraphAsync(string definitionVersionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to retrieve the pipeline graph corresponding to the specified <see cref="PipelineDefinitionHandle"/>.
    /// </summary>
    /// <param name="definitionHandle">The handle identifying the pipeline definition and its version.</param>
    /// <param name="cancellationToken">A token to cancel the asynchronous operation.</param>
    /// <returns>
    /// A <see cref="PipelineGraphFindResult"/> containing the pipeline graph and its associated definition,
    /// or indicating that they do not exist.
    /// </returns>
    Task<PipelineGraphFindResult> TryFindPipelineGraphAsync(PipelineDefinitionHandle definitionHandle, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to retrieve a <see cref="PipelineGraph"/> and its associated <see cref="PipelineDefinition"/>
    /// for the specified pipeline definition using the provided filter criteria.
    /// </summary>
    /// <param name="filter">The criteria used to filter and locate the pipeline definition and graph.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>
    /// A <see cref="PipelineGraphFindResult"/> containing the pipeline graph and its associated definition,
    /// or indicating that they do not exist.
    /// </returns>
    Task<PipelineGraphFindResult> TryFindPipelineGraphAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to find and retrieve a collection of <see cref="PipelineGraphFindResult"/> objects based on the specified <see cref="PipelineDefinitionFilter"/>.
    /// </summary>
    /// <param name="filter">The filter specifying criteria to identify the pipeline definitions to search for.</param>
    /// <param name="cancellationToken">A token to observe while waiting for the task to complete.</param>
    /// <returns>
    /// A collection of <see cref="PipelineGraphFindResult"/> instances, each containing a pipeline graph and its associated definition
    /// for every matching pipeline definition. If no matching pipeline definitions are found, the collection is empty.
    /// </returns>
    Task<IEnumerable<PipelineGraphFindResult>> TryFindPipelineGraphsAsync(PipelineDefinitionFilter filter, CancellationToken cancellationToken = default);
}