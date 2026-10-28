using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a pipeline canceler.
/// </summary>
public interface IPipelineCanceler
{
    /// <summary>
    /// Cancels the specified pipeline.
    /// </summary>
    /// <param name="pipelineGraph">The pipeline graph.</param>
    /// <param name="pipelineState">The pipeline state to cancel.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    Task<PipelineState> CancelPipelineAsync(PipelineGraph pipelineGraph, PipelineState pipelineState, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels the specified pipeline execution context.
    /// </summary>
    /// <param name="pipelineExecutionContext">The pipeline execution context to cancel.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    Task CancelPipelineAsync(PipelineExecutionContext pipelineExecutionContext, CancellationToken cancellationToken = default);
}