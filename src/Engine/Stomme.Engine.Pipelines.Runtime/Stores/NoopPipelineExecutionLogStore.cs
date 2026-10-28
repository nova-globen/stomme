using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;

namespace Stomme.Engine.Pipelines.Runtime.Stores;

public class NoopPipelineExecutionLogStore : IPipelineExecutionLogStore
{
    public Task AddAsync(PipelineExecutionLogRecord record, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task AddManyAsync(IEnumerable<PipelineExecutionLogRecord> records, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task SaveAsync(PipelineExecutionLogRecord record, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task SaveManyAsync(IEnumerable<PipelineExecutionLogRecord> records, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<PipelineExecutionLogRecord?> FindAsync(PipelineExecutionLogRecordFilter filter, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<PipelineExecutionLogRecord?>(null);
    }

    public Task<PipelineExecutionLogRecord?> FindAsync<TOrderBy>(PipelineExecutionLogRecordFilter filter, PipelineExecutionLogRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<PipelineExecutionLogRecord?>(null);
    }

    public Task<Page<PipelineExecutionLogRecord>> FindManyAsync(PipelineExecutionLogRecordFilter filter, PageArgs pageArgs, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Page<PipelineExecutionLogRecord>(new List<PipelineExecutionLogRecord>(0), 0));
    }

    public Task<Page<PipelineExecutionLogRecord>> FindManyAsync<TOrderBy>(PipelineExecutionLogRecordFilter filter, PageArgs pageArgs, PipelineExecutionLogRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Page<PipelineExecutionLogRecord>(new List<PipelineExecutionLogRecord>(0), 0));
    }

    public Task<long> DeleteManyAsync(PipelineExecutionLogRecordFilter filter, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(0L);
    }
}