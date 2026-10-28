using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime.Options;
using Stomme.Engine.Pipelines.State;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Creates <see cref="IPipelineHost"/> objects.
/// </summary>
[Obsolete("Use IPipelineRuntime, IPipelineRunner and IPipelineInvoker services instead.")]
public interface IPipelineHostFactory
{
    /// <summary>
    /// Creates a new <see cref="IPipelineHost"/> object.
    /// </summary>
    /// <param name="pipelineGraph">The pipeline.</param>
    /// <param name="options">The options to use when creating the pipeline host.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    Task<IPipelineHost> CreateAsync(PipelineGraph pipelineGraph, PipelineHostOptions? options = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Creates a new <see cref="IPipelineHost"/> object.
    /// </summary>
    /// <param name="pipelineGraph">The pipeline.</param>
    /// <param name="pipelineState">The pipeline state to initialize the pipeline host with.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    Task<IPipelineHost> CreateAsync(PipelineGraph pipelineGraph, PipelineState pipelineState, CancellationToken cancellationToken = default);
}