using Stomme.Engine.Pipelines.Runtime.Tasks;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Defines the contract for restarting pipelines in the runtime environment.
/// </summary>
/// <remarks>
/// This service is used by the <see cref="RestartInterruptedPipelinesTask"/> responsible for restarting interrupted pipelines.
/// </remarks>
public interface IPipelineRestarter
{
    /// <summary>
    /// Restarts a pipeline with the specified pipeline instance ID.
    /// </summary>
    /// <param name="pipelineInstanceId">The ID of the pipeline instance to restart.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    public Task RestartPipelineAsync(string pipelineInstanceId, CancellationToken cancellationToken = default);
}