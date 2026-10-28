using Stomme.Engine.Common.Models;
using Stomme.Engine.Common.Services;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;

namespace Stomme.Engine.Pipelines.Runtime.Stores;

/// <inheritdoc />
public class MemoryPipelineExecutionLogStore : IPipelineExecutionLogStore
{
    private readonly MemoryStore<PipelineExecutionLogRecord> _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryPipelineExecutionLogStore"/> class.
    /// </summary>
    public MemoryPipelineExecutionLogStore(MemoryStore<PipelineExecutionLogRecord> store)
    {
        _store = store;
    }

    /// <inheritdoc />
    public Task AddAsync(PipelineExecutionLogRecord record, CancellationToken cancellationToken = default)
    {
        _store.Add(record, x => x.Id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task AddManyAsync(IEnumerable<PipelineExecutionLogRecord> records, CancellationToken cancellationToken = default)
    {
        _store.AddMany(records, x => x.Id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SaveAsync(PipelineExecutionLogRecord record, CancellationToken cancellationToken = default)
    {
        _store.Save(record, x => x.Id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SaveManyAsync(IEnumerable<PipelineExecutionLogRecord> records, CancellationToken cancellationToken = default)
    {
        _store.SaveMany(records, x => x.Id);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PipelineExecutionLogRecord?> FindAsync(PipelineExecutionLogRecordFilter filter, CancellationToken cancellationToken = default)
    {
        var result = _store.Query(query => Filter(query, filter)).FirstOrDefault();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<PipelineExecutionLogRecord?> FindAsync<TOrderBy>(PipelineExecutionLogRecordFilter filter, PipelineExecutionLogRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        var result = _store.Query(query => Filter(query, filter).OrderBy(order)).FirstOrDefault();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<Page<PipelineExecutionLogRecord>> FindManyAsync(PipelineExecutionLogRecordFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
    {
        var count = _store.Query(query => Filter(query, filter)).LongCount();
        var result = _store.Query(query => Filter(query, filter).Paginate(pageArgs)).ToList();
        return Task.FromResult(Page.Of(result, count));
    }

    /// <inheritdoc />
    public Task<Page<PipelineExecutionLogRecord>> FindManyAsync<TOrderBy>(PipelineExecutionLogRecordFilter filter, PageArgs pageArgs, PipelineExecutionLogRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        var count = _store.Query(query => Filter(query, filter)).LongCount();
        var result = _store.Query(query => Filter(query, filter).OrderBy(order).Paginate(pageArgs)).ToList();
        return Task.FromResult(Page.Of(result, count));
    }

    /// <inheritdoc />
    public Task<long> DeleteManyAsync(PipelineExecutionLogRecordFilter filter, CancellationToken cancellationToken = default)
    {
        var records = _store.Query(query => Filter(query, filter)).ToList();
        _store.DeleteMany(records, x => x.Id);
        return Task.FromResult(records.LongCount());
    }

    private IQueryable<PipelineExecutionLogRecord> Filter(IQueryable<PipelineExecutionLogRecord> queryable, PipelineExecutionLogRecordFilter filter) => filter.Apply(queryable);
}