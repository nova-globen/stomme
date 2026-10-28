using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Refreshes all pipelines by re-indexing their triggers.
/// </summary>
public interface IPipelineDefinitionsRefresher
{
    /// <summary>
    /// Refreshes all pipelines by re-indexing their triggers.
    /// </summary>
    Task<RefreshPipelineDefinitionsResponse> RefreshPipelineDefinitionsAsync(RefreshPipelineDefinitionsRequest request, CancellationToken cancellationToken = default);
}