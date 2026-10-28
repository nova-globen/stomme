using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Stores step execution records.
/// </summary>
public interface IStepExecutionStore : ILogRecordStore<StepExecutionRecord>
{
    /// <summary>
    /// Adds or updates the specified <see cref="StepExecutionRecord"/> in the persistence store.
    /// </summary>
    /// <param name="record">The step execution record.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <remarks>
    /// If the record does not already exist, it is added to the store; if it does exist, its existing entry is updated.
    /// </remarks>
    Task SaveAsync(StepExecutionRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds a step execution record matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter to apply when searching for the step execution record.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>The step execution record that matches the filter, or null if no match is found.</returns>
    Task<StepExecutionRecord?> FindAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds all step execution records matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter to use when finding step execution records.</param>
    /// <param name="order">The order to use when finding step execution records.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <typeparam name="TOrderBy">The type of the order by field.</typeparam>
    /// <returns>A collection of step execution records.</returns>
    Task<IEnumerable<StepExecutionRecord>> FindManyAsync<TOrderBy>(StepExecutionRecordFilter filter, StepExecutionRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Finds all step execution records matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter to use when finding step execution records.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>A collection of step execution records.</returns>
    Task<IEnumerable<StepExecutionRecord>> FindManyAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Finds all step execution record matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter to use when finding step execution records.</param>
    /// <param name="order">The order to use when finding step execution records.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <typeparam name="TOrderBy">The type of the order by field.</typeparam>
    /// <returns>A collection of step execution record summaries.</returns>
    Task<IEnumerable<StepExecutionRecordSummary>> FindManySummariesAsync<TOrderBy>(StepExecutionRecordFilter filter, StepExecutionRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Finds all step execution records matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter to use when finding step execution records.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>A collection of step execution record summaries.</returns>
    Task<IEnumerable<StepExecutionRecordSummary>> FindManySummariesAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts all step execution records matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter to use when counting step execution records.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>The number of step execution records matching the specified filter.</returns>
    Task<long> CountAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Deletes all step execution records matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    Task<long> DeleteManyAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default);
}
