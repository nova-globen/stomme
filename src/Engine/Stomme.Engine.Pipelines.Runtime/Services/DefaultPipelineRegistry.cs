using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Materializers;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class DefaultPipelineRegistry(IPipelineDefinitionStorePopulator populator) : IPipelineRegistry
{
    /// <inheritdoc />
    public async Task RegisterAsync(Pipeline pipeline, CancellationToken cancellationToken = default)
    {
        var materializedPipeline = new MaterializedPipeline(pipeline, nameof(DefaultPipelineRegistry), TypedPipelineMaterializer.MaterializerName);
        var result = await populator.AddAsync(materializedPipeline, cancellationToken).ConfigureAwait(false);
        result.ThrowIfFailure();
    }
}