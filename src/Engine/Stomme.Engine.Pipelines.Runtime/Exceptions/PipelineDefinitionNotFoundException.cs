using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Runtime.Exceptions;

public class PipelineDefinitionNotFoundException(string message, PipelineDefinitionHandle pipelineDefinitionHandle) : Exception(message)
{
    public PipelineDefinitionHandle PipelineDefinitionHandle { get; } = pipelineDefinitionHandle;
}