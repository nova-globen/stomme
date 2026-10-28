using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;

namespace Stomme.Engine.Pipelines.Runtime.Stores;

public class NoopStepExecutionStore : IStepExecutionStore
{
    public Task SaveAsync(StepExecutionRecord record, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task SaveManyAsync(IEnumerable<StepExecutionRecord> records, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task AddManyAsync(IEnumerable<StepExecutionRecord> records, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    public Task<StepExecutionRecord?> FindAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<StepExecutionRecord?>(null);
    }

    public Task<IEnumerable<StepExecutionRecord>> FindManyAsync<TOrderBy>(StepExecutionRecordFilter filter, StepExecutionRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IEnumerable<StepExecutionRecord>>(Array.Empty<StepExecutionRecord>());
    }

    public Task<IEnumerable<StepExecutionRecord>> FindManyAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IEnumerable<StepExecutionRecord>>(Array.Empty<StepExecutionRecord>());
    }

    public Task<IEnumerable<StepExecutionRecordSummary>> FindManySummariesAsync<TOrderBy>(StepExecutionRecordFilter filter, StepExecutionRecordOrder<TOrderBy> order, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IEnumerable<StepExecutionRecordSummary>>(Array.Empty<StepExecutionRecordSummary>());
    }

    public Task<IEnumerable<StepExecutionRecordSummary>> FindManySummariesAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IEnumerable<StepExecutionRecordSummary>>(Array.Empty<StepExecutionRecordSummary>());
    }

    public Task<long> CountAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(0L);
    }

    public Task<long> DeleteManyAsync(StepExecutionRecordFilter filter, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(0L);
    }

    public Task<Page<StepExecutionRecord>> GetExecutionChainAsync(
        string stepExecutionId,
        bool includeCrossPipelineChain = true,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Page.Empty<StepExecutionRecord>());
    }
}
