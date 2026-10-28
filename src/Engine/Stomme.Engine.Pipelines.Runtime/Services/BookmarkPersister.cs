using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using Stomme.Engine.Pipelines.Runtime.Requests;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class BookmarksPersister(IBookmarkUpdater bookmarkUpdater, INotificationSender notificationSender) : IBookmarksPersister
{
    /// <inheritdoc />
    public async Task PersistBookmarksAsync(UpdateBookmarksRequest updateBookmarksRequest)
    {
        await bookmarkUpdater.UpdateBookmarksAsync(updateBookmarksRequest).ConfigureAwait(false);

        // Publish domain event.
        await notificationSender.SendAsync(new PipelineBookmarksIndexed(new(
            updateBookmarksRequest.PipelineExecutionContext,
            updateBookmarksRequest.Diff.Added, 
            updateBookmarksRequest.Diff.Removed, 
            updateBookmarksRequest.Diff.Unchanged))).ConfigureAwait(false);

        // Publish domain event.
        await notificationSender.SendAsync(new PipelineBookmarksPersisted(updateBookmarksRequest.Diff)).ConfigureAwait(false);
    }
}