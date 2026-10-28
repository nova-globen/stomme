using Stomme.Engine.Common;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Runtime.Tasks;

/// <summary>
/// Periodically purges the bookmark queue of old items.
/// </summary>
[UsedImplicitly]
public class PurgeBookmarkQueueRecurringTask(IBookmarkQueuePurger bookmarkQueueWorker) : RecurringTask
{
    public override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return bookmarkQueueWorker.PurgeAsync(stoppingToken);
    }
}