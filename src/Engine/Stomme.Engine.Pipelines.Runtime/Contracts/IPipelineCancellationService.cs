using Stomme.Engine.Common.Models;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Service wrapper for cancelling multiple pipeline instances.
/// </summary>
public interface IPipelineCancellationService
{
    /// <summary>
    /// Cancels a pipeline instance.
    /// </summary>
    /// <remarks>Also cancels all children</remarks>
    Task<bool> CancelPipelineAsync(string pipelineInstanceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels pipeline executions with the specified pipeline instance ID.
    /// </summary>
    /// <remarks>Also cancels all children</remarks>
    Task<int> CancelPipelinesAsync(IEnumerable<string> pipelineInstanceIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels all pipeline instances by definition version ID.
    /// </summary>
    /// <remarks>Also cancels all children</remarks>
    Task<int> CancelPipelineByDefinitionVersionAsync(string definitionVersionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels all pipelines that match the specified pipeline definition by its ID and version.
    /// </summary>
    /// <remarks>Also cancels all children</remarks>
    Task<int> CancelPipelineByDefinitionAsync(string definitionId, VersionOptions versionOptions, CancellationToken cancellationToken = default);

}