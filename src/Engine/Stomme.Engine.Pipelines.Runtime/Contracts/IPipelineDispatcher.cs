using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Posts a message to a queue to invoke a specified pipeline or trigger a set of pipelines.
/// </summary>
public interface IPipelineDispatcher
{
    /// <summary>
    /// Dispatches a request to execute the specified pipeline definition.
    /// </summary>
    Task<DispatchPipelineResponse> DispatchAsync(DispatchPipelineDefinitionRequest request, DispatchPipelineOptions? options, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Dispatches a request to execute the specified pipeline instance.
    /// </summary>
    Task<DispatchPipelineResponse> DispatchAsync(DispatchPipelineInstanceRequest request, DispatchPipelineOptions? options, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Starts all pipelines and resumes existing pipeline instances based on the specified step type and bookmark payload.
    /// </summary>
    Task<DispatchPipelineResponse> DispatchAsync(DispatchTriggerPipelinesRequest request, DispatchPipelineOptions? options, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Resumes the pipeline waiting for the specified bookmark.
    /// </summary>
    Task<DispatchPipelineResponse> DispatchAsync(DispatchResumePipelinesRequest request, DispatchPipelineOptions? options, CancellationToken cancellationToken = default);
}