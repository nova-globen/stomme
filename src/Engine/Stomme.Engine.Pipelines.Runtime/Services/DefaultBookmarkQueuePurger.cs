using Stomme.Engine.Common;
using Stomme.Engine.Common.Entities;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Options;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Pipelines.Runtime;

[UsedImplicitly]
public class DefaultBookmarkQueuePurger(IBookmarkQueueStore store, ISystemClock systemClock, IOptions<BookmarkQueuePurgeOptions> options, ILogger<DefaultBookmarkQueuePurger> logger) : IBookmarkQueuePurger
{
    public async Task PurgeAsync(CancellationToken cancellationToken = default)
    {
        var currentPage = 0;
        var now = systemClock.UtcNow;
        var thresholdDate = now - options.Value.Ttl;

        logger.LogDebug("Purging bookmark queue items older than {ThresholdDate}.", thresholdDate);

        while (true)
        {
            var pageArgs = PageArgs.FromPage(currentPage, options.Value.BatchSize);
            var filter = new BookmarkQueueFilter
            {
                CreatedAtLessThan = thresholdDate
            };
            var order = new BookmarkQueueItemOrder<DateTimeOffset>(x => x.CreatedAt, OrderDirection.Ascending);
            var page = await store.PageAsync(pageArgs, filter, order, cancellationToken).ConfigureAwait(false);
            var items = page.Items;

            if (items.Count == 0)
                break;

            var ids = items.Select(x => x.Id).ToList();
            await store.DeleteAsync(new BookmarkQueueFilter
            {
                Ids = ids
            }, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Purged {Count} bookmark queue items.", items.Count);

            currentPage++;
        }

        logger.LogDebug("Finished purging bookmark queue items.");
    }
}