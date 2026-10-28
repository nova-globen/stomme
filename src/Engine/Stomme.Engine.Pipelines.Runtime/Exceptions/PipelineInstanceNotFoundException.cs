namespace Stomme.Engine.Pipelines.Runtime.Exceptions;

public class PipelineInstanceNotFoundException(string message, string instanceId) : Exception(message)
{
    public string InstanceId { get; } = instanceId;
}