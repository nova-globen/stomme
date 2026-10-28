namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// A service for reading step execution logs.
/// </summary>
public interface IStepExecutionStatsService
{
    /// <summary>
    /// Gets execution stats for the specified steps.
    /// </summary>
    /// <param name="pipelineInstanceId">The pipeline instance ID.</param>
    /// <param name="stepNodeIds">The step node IDs.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>The step execution stats.</returns>
    Task<IEnumerable<StepExecutionStats>> GetStatsAsync(string pipelineInstanceId, IEnumerable<string> stepNodeIds, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Gets execution stats for the specified step.
    /// </summary>
    /// <param name="pipelineInstanceId">The pipeline instance ID.</param>
    /// <param name="stepNodeId">The step node ID.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>The step execution stats.</returns>
    Task<StepExecutionStats> GetStatsAsync(string pipelineInstanceId, string stepNodeId, CancellationToken cancellationToken = default);
}