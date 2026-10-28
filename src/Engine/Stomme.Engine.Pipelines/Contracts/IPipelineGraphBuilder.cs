using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Builds a pipeline graph from a pipeline.
/// </summary>
public interface IPipelineGraphBuilder
{
    /// <summary>
    /// Builds a pipeline graph from a pipeline.
    /// </summary>
    Task<PipelineGraph> BuildAsync(Pipeline pipeline, CancellationToken cancellationToken = default);
}