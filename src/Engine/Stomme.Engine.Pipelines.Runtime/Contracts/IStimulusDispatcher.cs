using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;

namespace Stomme.Engine.Pipelines.Runtime;

public interface IStimulusDispatcher
{
    /// <summary>
    /// Dispatches stimulus. This could result in new pipeline instances being started as well as existing pipeline instances being resumed.
    /// </summary>
    Task<DispatchStimulusResponse> SendAsync(DispatchStimulusRequest request, CancellationToken cancellationToken = default);
}