using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Entities;
using Stomme.Engine.Pipelines.Management.Mappers;

namespace Stomme.Engine.Pipelines.Management.Materializers;

/// <summary>
/// A pipeline materializer that deserializes pipelines created in C# code.
/// </summary>
public class TypedPipelineMaterializer(PipelineDefinitionMapper pipelineDefinitionMapper) : IPipelineMaterializer
{
    /// <summary>
    /// The name of the materializer.
    /// </summary>
    public const string MaterializerName = "Typed";

    /// <inheritdoc />
    public string Name => MaterializerName;

    /// <inheritdoc />
    public ValueTask<Pipeline> MaterializeAsync(PipelineDefinition definition, CancellationToken cancellationToken)
    {
        var pipeline = ToPipeline(definition);
        return new ValueTask<Pipeline>(pipeline);
    }

    private Pipeline ToPipeline(PipelineDefinition definition) => pipelineDefinitionMapper.Map(definition);
}