namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a service that looks up trigger-bound pipelines.
/// </summary>
public interface ITriggerBoundPipelineService
{
    /// <summary>
    /// Finds trigger-bound pipelines by step type name and stimulus.
    /// </summary>
    Task<IEnumerable<TriggerBoundPipeline>> FindManyAsync(string stepTypeName, object stimulus, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Finds trigger-bound pipelines by stimulus hash.
    /// </summary>
    Task<IEnumerable<TriggerBoundPipeline>> FindManyAsync(string stimulusHash, CancellationToken cancellationToken = default);
}