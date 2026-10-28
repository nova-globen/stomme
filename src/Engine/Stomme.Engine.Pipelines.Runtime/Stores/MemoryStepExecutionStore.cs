using Stomme.Engine.Common.Services;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;

namespace Stomme.Engine.Pipelines.Runtime.Stores;

/// <summary>
/// Stores step execution records in memory.
/// </summary>
public class MemoryStepExecutionStore : IStepExecutionStore
{
    private readonly MemoryStore<StepExecutionRecord> _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryStepExecutionStore"/> class.
    /// </summary>
    public MemoryStepExecutionStore(MemoryStore<StepExecutionRecord> store)
    {
        _store = store;
    }
    
    /// <inheritdoc />
    public Task SaveAsync(StepExecutionRecord record, CancellationToken cancellationToken = default)
    {
        _store.Save(record, x => x.Id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SaveManyAsync(IEnumerable<StepExecutionRecord> records, CancellationToken cancellationToken = default)
    {
        _store.SaveMany(records, x => x.Id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AddManyAsync(IEnumerable<StepExecutionRecord> records, CancellationToken cancellationToken = default)
    {
        _store.AddMany(records, x => x.Id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<StepExecutionRecord?> FindAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
    {
        var result = _store.Query(query => Filter(query, filter)).FirstOrDefault();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IEnumerable<StepExecutionRecord>> FindManyAsync<TOrderBy>(StepExecutionRecordFilter filter, StepExecutionRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        var result = _store.Query(query => Filter(query, filter).OrderBy(order)).ToList().AsEnumerable();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IEnumerable<StepExecutionRecord>> FindManyAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
    {
        var result = _store.Query(query => Filter(query, filter)).ToList().AsEnumerable();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<StepExecutionRecordSummary>> FindManySummariesAsync<TOrderBy>(StepExecutionRecordFilter filter, StepExecutionRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        var entities = await FindManyAsync(filter, order, cancellationToken).ConfigureAwait(false);
        return entities.Select(StepExecutionRecordSummary.FromRecord);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<StepExecutionRecordSummary>> FindManySummariesAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
    {
        var entities = await FindManyAsync(filter, cancellationToken).ConfigureAwait(false);
        return entities.Select(StepExecutionRecordSummary.FromRecord);
    }

    /// <inheritdoc />
    public Task<long> CountAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
    {
        var count = _store.Query(query => Filter(query, filter)).LongCount();
        return Task.FromResult(count);
    }

    /// <inheritdoc />
    public Task<long> DeleteManyAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
    {
        var records = _store.Query(query => Filter(query, filter)).ToList();
        _store.DeleteMany(records, x => x.Id);
        return Task.FromResult(records.LongCount());
    }


    private static IQueryable<StepExecutionRecord> Filter(IQueryable<StepExecutionRecord> queryable, StepExecutionRecordFilter filter) => filter.Apply(queryable);
}
