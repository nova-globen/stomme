using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Posts a message to a topic to cancel a specified set of pipelines.
/// </summary>
public interface IPipelineCancellationDispatcher
{
    /// <summary>
    /// Cancels the specified pipeline instances.
    /// </summary>
    Task<DispatchCancelPipelinesResponse> DispatchAsync(DispatchCancelPipelineRequest request, CancellationToken cancellationToken = default);
}