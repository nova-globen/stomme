namespace Stomme.Engine.Pipelines.Management.Exceptions;

public class PipelineMaterializerNotFoundException(string materializerName, string message = "Materializer not found. The materializer may be disabled or not registered") : Exception(message)
{
    public string MaterializerName { get; } = materializerName;
}