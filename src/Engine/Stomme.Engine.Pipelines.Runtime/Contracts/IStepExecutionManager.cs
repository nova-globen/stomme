using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Manages step execution records.
/// </summary>
public interface IStepExecutionManager
{
    /// <summary>
    /// Deletes the records matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter to use when deleting step execution records.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>The number of step execution records deleted.</returns>
    Task<long> DeleteManyAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the specified step execution record.
    /// </summary>
    Task SaveAsync(StepExecutionRecord record, CancellationToken cancellationToken = default);
}