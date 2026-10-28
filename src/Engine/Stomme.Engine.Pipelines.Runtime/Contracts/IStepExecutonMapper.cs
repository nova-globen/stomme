using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Maps step execution contexts to step execution records.
/// </summary>
public interface IStepExecutionMapper
{
    /// <summary>
    /// Maps a step execution context to a step execution record.
    /// </summary>
    Task<StepExecutionRecord> MapAsync(StepExecutionContext source);
}