using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Responses;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Pipelines.Runtime.Contracts;

/// <summary>
/// Provides a set of extension methods for <see cref="IPipelineDispatcher"/>.
/// </summary>
public static class PipelineDispatcherExtensions
{
    /// <summary>
    /// Dispatches a request to execute the specified pipeline definition.
    /// </summary>
    public static Task<DispatchPipelineResponse> DispatchAsync(this IPipelineDispatcher pipelineDispatcher, DispatchPipelineDefinitionRequest request, CancellationToken cancellationToken = default)
    {
        return pipelineDispatcher.DispatchAsync(request, new(), cancellationToken);
    }

    /// <summary>
    /// Dispatches a request to execute the specified pipeline instance.
    /// </summary>
    public static Task<DispatchPipelineResponse> DispatchAsync(this IPipelineDispatcher pipelineDispatcher, DispatchPipelineInstanceRequest request, CancellationToken cancellationToken = default)
    {
        return pipelineDispatcher.DispatchAsync(request, new(), cancellationToken);
    }

    /// <summary>
    /// Starts all pipelines and resumes existing pipeline instances based on the specified step type and bookmark payload.
    /// </summary>
    public static Task<DispatchPipelineResponse> DispatchAsync(this IPipelineDispatcher pipelineDispatcher, DispatchTriggerPipelinesRequest request, CancellationToken cancellationToken = default)
    {
        return pipelineDispatcher.DispatchAsync(request, new(), cancellationToken);
    }

    /// <summary>
    /// Resumes the pipeline waiting for the specified bookmark.
    /// </summary>
    public static Task<DispatchPipelineResponse> DispatchAsync(this IPipelineDispatcher pipelineDispatcher, DispatchResumePipelinesRequest request, CancellationToken cancellationToken = default)
    {
        return pipelineDispatcher.DispatchAsync(request, new(), cancellationToken);
    }
}