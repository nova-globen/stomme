using Stomme.Engine.Pipelines.Steps;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Registers pipelines.
/// </summary>
public interface IPipelineRegistry
{
    /// <summary>
    /// Registers a pipeline.
    /// </summary>
    /// <param name="pipeline">The pipeline to register.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    Task RegisterAsync(Pipeline pipeline, CancellationToken cancellationToken = default);
}