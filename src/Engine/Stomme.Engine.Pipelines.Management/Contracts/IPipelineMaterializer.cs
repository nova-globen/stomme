using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Entities;

namespace Stomme.Engine.Pipelines.Management;

/// <summary>
/// A service that can materialize a pipeline from a pipeline definition.
/// </summary>
public interface IPipelineMaterializer
{
    /// <summary>
    /// The name of the materializer.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Materializes a pipeline from a pipeline definition.
    /// </summary>
    /// <param name="definition">The pipeline definition.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The materialized pipeline.</returns>
    ValueTask<Pipeline> MaterializeAsync(PipelineDefinition definition, CancellationToken cancellationToken = default);
}