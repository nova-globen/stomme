using Stomme.Engine.Common;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Runtime;

public class StoreBookmarkQueue(
    IBookmarkQueueStore store, 
    IBookmarkQueueSignaler bookmarkQueueSignaler, 
    ISystemClock systemClock, 
    IIdentityGenerator identityGenerator,
    ILogger<StoreBookmarkQueue> logger) : IBookmarkQueue
{
    public async Task EnqueueAsync(NewBookmarkQueueItem item, CancellationToken cancellationToken = default)
    {
        var entity = new BookmarkQueueItem
        {
            Id = identityGenerator.GenerateId(),
            PipelineInstanceId = item.PipelineInstanceId,
            BookmarkId = item.BookmarkId,
            CorrelationId = item.CorrelationId,
            StimulusHash = item.StimulusHash,
            StepInstanceId = item.StepInstanceId,
            StepTypeName = item.StepTypeName,
            Options = item.Options,
            CreatedAt = systemClock.UtcNow,
        };

        logger.LogDebug("Enqueuing bookmark queue item {BookmarkQueueItemId} with bookmark {BookmarkId} and stimulus {StimulusHash}", entity.Id, entity.BookmarkId, entity.StimulusHash);
        
        await store.AddAsync(entity, cancellationToken).ConfigureAwait(false);

        // Trigger the bookmark queue processor.
        await bookmarkQueueSignaler.TriggerAsync(cancellationToken).ConfigureAwait(false);
    }
}