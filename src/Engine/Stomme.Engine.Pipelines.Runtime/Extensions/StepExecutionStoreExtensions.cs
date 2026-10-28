using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Provides extension methods for <see cref="IStepExecutionStore"/>.
/// </summary>
public static class StepExecutionStoreExtensions
{
    /// <summary>
    /// Retrieves the execution chain for the specified step execution record by traversing the SchedulingStepExecutionId chain.
    /// Returns records ordered from root (depth 0) to the specified record.
    /// </summary>
    public static async Task<Page<StepExecutionRecord>> GetExecutionChainAsync(
        this IStepExecutionStore store,
        string stepExecutionId,
        bool includeCrossPipelineChain = true,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        var chain = new List<StepExecutionRecord>();
        var visited = new HashSet<string>();
        var loadedRecords = new Dictionary<string, StepExecutionRecord>();
        var currentId = stepExecutionId;

        while (currentId != null && visited.Add(currentId))
        {
            if (!loadedRecords.TryGetValue(currentId, out var record))
            {
                record = await store.FindAsync(new() { Id = currentId }, cancellationToken).ConfigureAwait(false);
                
                if (record == null)
                    break;

                loadedRecords.TryAdd(currentId, record);
            }

            chain.Add(record);

            if (!includeCrossPipelineChain && record.SchedulingPipelineInstanceId != null)
                break;

            currentId = record.SchedulingStepExecutionId;
        }

        chain.Reverse();

        var totalCount = chain.Count;

        if (skip.HasValue)
            chain = chain.Skip(skip.Value).ToList();
        
        if (take.HasValue)
            chain = chain.Take(take.Value).ToList();

        return Page.Of(chain, totalCount);
    }
}
