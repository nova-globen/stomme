using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Runtime.Exceptions;

[Obsolete("Use PipelineDefinitionNotFoundException instead.")]
public class PipelineGraphNotFoundException(string message, PipelineDefinitionHandle pipelineDefinitionHandle) : Exception(message)
{
    public PipelineDefinitionHandle PipelineDefinitionHandle { get; } = pipelineDefinitionHandle;
}