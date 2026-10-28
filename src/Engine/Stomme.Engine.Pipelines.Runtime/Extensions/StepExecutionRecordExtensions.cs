using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Extensions;

/// <summary>
/// Provides extension methods for <see cref="StepExecutionRecord"/>.
/// </summary>
public static class StepExecutionRecordExtensions
{
    /// <summary>
    /// Retrieves the execution chain for the specified step execution record by using the store.
    /// Returns records ordered from root (depth 0) to the specified record.
    /// </summary>
    /// <param name="record">The step execution record to retrieve the chain for.</param>
    /// <param name="store">The step execution store to use for retrieving the chain.</param>
    /// <param name="includeCrossPipelineChain">If true (default), follows SchedulingPipelineInstanceId across pipeline boundaries.</param>
    /// <param name="skip">The number of items to skip (for pagination).</param>
    /// <param name="take">The maximum number of items to return (for pagination).</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>A paginated result containing the call stack records, ordered from root to leaf.</returns>
    public static Task<Page<StepExecutionRecord>> GetExecutionChainAsync(
        this StepExecutionRecord record,
        IStepExecutionStore store,
        bool includeCrossPipelineChain = true,
        int? skip = null,
        int? take = null,
        CancellationToken cancellationToken = default)
    {
        return store.GetExecutionChainAsync(record.Id, includeCrossPipelineChain, skip, take, cancellationToken);
    }
}
