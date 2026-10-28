using System.Text.Json;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Mappers;
using Stomme.Engine.Pipelines.Management.Models;

namespace Stomme.Engine.Pipelines.Management.Services;

/// <summary>
/// A serializer that parses JSON.
/// </summary>
public class PipelineSerializer(IApiSerializer apiSerializer, PipelineDefinitionMapper pipelineDefinitionMapper) : IPipelineSerializer
{
    private JsonSerializerOptions? _writeOptions;

    /// <inheritdoc />
    public string Serialize(Pipeline pipeline)
    {
        var model = pipelineDefinitionMapper.Map(pipeline);
        var serializerOptions = GetWriteOptionsInternal();
        return JsonSerializer.Serialize(model, serializerOptions);
    }

    /// <inheritdoc />
    public Pipeline Deserialize(string serializedPipeline)
    {
        var model = apiSerializer.Deserialize<PipelineDefinitionModel>(serializedPipeline);
        return pipelineDefinitionMapper.Map(model);
    }

    private JsonSerializerOptions GetWriteOptionsInternal()
    {
        if (_writeOptions != null)
            return _writeOptions;

        var options = apiSerializer.GetOptions();
        return _writeOptions = options;
    }
}