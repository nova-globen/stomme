using Stomme.Engine.Common.Entities;
using Stomme.Engine.Common.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.OrderDefinitions;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Runtime;

public class BookmarkQueueProcessor(IBookmarkQueueStore store, IPipelineResumer pipelineResumer, ILogger<BookmarkQueueProcessor> logger) : IBookmarkQueueProcessor
{
    public async Task ProcessAsync(CancellationToken cancellationToken = default)
    {
        var batchSize = 50;
        var offset = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var pageArgs = PageArgs.FromRange(offset, batchSize);
            var page = await store.PageAsync(pageArgs, new BookmarkQueueItemOrder<DateTimeOffset>(x => x.CreatedAt, OrderDirection.Ascending), cancellationToken).ConfigureAwait(false);

            await ProcessPageAsync(page, cancellationToken).ConfigureAwait(false);

            if (page.Items.Count < batchSize)
                break;

            offset += batchSize;
        }
    }

    private async Task ProcessPageAsync(Page<BookmarkQueueItem> page, CancellationToken cancellationToken = default)
    {
        foreach (var bookmarkQueueItem in page.Items) 
            await ProcessItemAsync(bookmarkQueueItem, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessItemAsync(BookmarkQueueItem item, CancellationToken cancellationToken = default)
    {
        var filter = item.CreateBookmarkFilter();
        var options = item.Options;
        
        logger.LogDebug("Processing bookmark queue item {BookmarkQueueItemId} for pipeline instance {PipelineInstanceId} for step type {StepType}", item.Id, item.PipelineInstanceId, item.StepTypeName);
        
        var responses = (await pipelineResumer.ResumeAsync(filter, options, cancellationToken).ConfigureAwait(false)).ToList();

        if (responses.Count > 0)
        {
            logger.LogDebug("Successfully resumed {PipelineCount} pipeline instances using stimulus {StimulusHash} for step type {StepType}", responses.Count, item.StimulusHash, item.StepTypeName);
            await store.DeleteAsync(item.Id, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            logger.LogDebug("No matching bookmarks found for bookmark queue item {BookmarkQueueItemId} for pipeline instance {PipelineInstanceId} for step type {StepType} with stimulus {StimulusHash}", item.Id, item.PipelineInstanceId, item.StepTypeName, item.StimulusHash);
        }
    }
}