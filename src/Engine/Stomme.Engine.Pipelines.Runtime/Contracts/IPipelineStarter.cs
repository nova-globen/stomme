namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents an interface responsible for starting pipelines.
/// Provides a method to start a pipeline based on the provided request.
/// </summary>
public interface IPipelineStarter
{
    public Task<StartPipelineResponse> StartPipelineAsync(StartPipelineRequest request, CancellationToken cancellationToken = default);
}