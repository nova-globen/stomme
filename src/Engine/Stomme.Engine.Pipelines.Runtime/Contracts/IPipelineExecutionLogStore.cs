using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a store of <see cref="PipelineExecutionLogRecord"/>.
/// </summary>
public interface IPipelineExecutionLogStore : ILogRecordStore<PipelineExecutionLogRecord>
{
    /// <summary>
    /// Adds the specified <see cref="PipelineExecutionLogRecord"/> to te persistence store.
    /// </summary>
    Task AddAsync(PipelineExecutionLogRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds or updates the specified <see cref="PipelineExecutionLogRecord"/> in the persistence store.
    /// </summary>
    /// <remarks>
    /// If the record does not already exist, it is added to the store; if it does exist, its existing entry is updated.
    /// </remarks>
    Task SaveAsync(PipelineExecutionLogRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the first pipeline execution log record matching the specified filter.
    /// </summary>
    Task<PipelineExecutionLogRecord?> FindAsync(PipelineExecutionLogRecordFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the first pipeline execution log record matching the specified filter.
    /// </summary>
    Task<PipelineExecutionLogRecord?> FindAsync<TOrderBy>(PipelineExecutionLogRecordFilter filter, PipelineExecutionLogRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a set of pipeline execution log records matching the specified filter.
    /// </summary>
    Task<Page<PipelineExecutionLogRecord>> FindManyAsync(PipelineExecutionLogRecordFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a set of pipeline execution log records matching the specified filter.
    /// </summary>
    Task<Page<PipelineExecutionLogRecord>> FindManyAsync<TOrderBy>(PipelineExecutionLogRecordFilter filter, PageArgs pageArgs, PipelineExecutionLogRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all pipeline execution log records matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>The number of deleted records.</returns>
    Task<long> DeleteManyAsync(PipelineExecutionLogRecordFilter filter, CancellationToken cancellationToken = default);
}