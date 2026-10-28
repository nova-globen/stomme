using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Mappers;

namespace Stomme.Engine.Pipelines.Management.Materializers;

/// <summary>
/// Materializes a <see cref="Pipeline"/> from a <see cref="PipelineDefinition"/>'s JSON data.
/// </summary>
public class JsonPipelineMaterializer : IPipelineMaterializer
{
    private readonly PipelineDefinitionMapper _pipelineDefinitionMapper;

    /// <summary>
    /// The name of the materializer.
    /// </summary>
    public const string MaterializerName = "Json";

    /// <inheritdoc />
    public string Name => MaterializerName;

    /// <summary>
    /// Initializes a new instance of the <see cref="JsonPipelineMaterializer"/> class.
    /// </summary>
    public JsonPipelineMaterializer(PipelineDefinitionMapper pipelineDefinitionMapper)
    {
        _pipelineDefinitionMapper = pipelineDefinitionMapper;
    }

    /// <inheritdoc />
    public ValueTask<Pipeline> MaterializeAsync(PipelineDefinition definition, CancellationToken cancellationToken)
    {
        var pipeline = ToPipeline(definition);
        return new(pipeline);
    }

    private Pipeline ToPipeline(PipelineDefinition definition) => _pipelineDefinitionMapper.Map(definition);
}