using Stomme.Engine.Common.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class StepExecutionStatsService : IStepExecutionStatsService
{
    private readonly IStepExecutionStore _store;

    /// <summary>
    /// Initializes a new instance of the <see cref="StepExecutionStatsService"/> class.
    /// </summary>
    public StepExecutionStatsService(IStepExecutionStore store)
    {
        _store = store;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<StepExecutionStats>> GetStatsAsync(string pipelineInstanceId, IEnumerable<string> stepNodeIds, CancellationToken cancellationToken = default)
    {
        var filter = new StepExecutionRecordFilter
        {
            PipelineInstanceId = pipelineInstanceId,
            StepNodeIds = stepNodeIds?.ToList()
        };
        var order = new StepExecutionRecordOrder<DateTimeOffset>(x => x.StartedAt, OrderDirection.Ascending);
        var records = (await _store.FindManySummariesAsync(filter, order, cancellationToken).ConfigureAwait(false)).ToList();
        var groupedRecords = records.GroupBy(x => x.StepNodeId).ToList();
        var stats = groupedRecords.Select(grouping =>
        {
            var first = grouping.First();
            var last = grouping.Last();
            return new StepExecutionStats
            {
                StepNodeId = grouping.Key,
                StepId = first.StepId,
                StartedCount = grouping.Count(),
                CompletedCount = grouping.Count(x => x.CompletedAt != null),
                UncompletedCount = grouping.Count(x => x.CompletedAt == null),
                IsBlocked = grouping.Any(x => x.HasBookmarks),
                IsFaulted = grouping.Any(x => x.Status == StepStatus.Faulted),
                AggregateFaultCount = last.AggregateFaultCount,
                Metadata = last.Metadata
            };
        }).ToList();

        return stats;
    }

    /// <inheritdoc />
    public async Task<StepExecutionStats> GetStatsAsync(string pipelineInstanceId, string stepNodeId, CancellationToken cancellationToken = default)
    {
        var stats = (await GetStatsAsync(pipelineInstanceId, [stepNodeId], cancellationToken).ConfigureAwait(false)).FirstOrDefault();

        return stats ?? new StepExecutionStats
        {
            StepNodeId = stepNodeId
        };
    }
}