using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Notifications;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Default implementation of <see cref="IBookmarkManager"/>.
/// </summary>
public class DefaultBookmarkManager(IBookmarkStore bookmarkStore, INotificationSender notificationSender, ILogger<DefaultBookmarkManager> logger)
    : IBookmarkManager
{
    /// <inheritdoc />
    public async Task<long> DeleteManyAsync(BookmarkFilter filter, CancellationToken cancellationToken = default)
    {
        var bookmarks = (await bookmarkStore.FindManyAsync(filter, cancellationToken).ConfigureAwait(false)).ToList();
        await notificationSender.SendAsync(new BookmarksDeleting(bookmarks), cancellationToken).ConfigureAwait(false);
        var count = await bookmarkStore.DeleteAsync(filter, cancellationToken).ConfigureAwait(false);
        await notificationSender.SendAsync(new BookmarksDeleted(bookmarks), cancellationToken).ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var bookmark in bookmarks)
                logger.LogDebug("Deleted bookmark {BookmarkId} for pipeline {PipelineInstanceId}", bookmark.Id, bookmark.PipelineInstanceId);
        }

        return count;
    }

    /// <inheritdoc />
    public async Task SaveAsync(StoredBookmark bookmark, CancellationToken cancellationToken = default)
    {
        await notificationSender.SendAsync(new BookmarkSaving(bookmark), cancellationToken).ConfigureAwait(false);
        await bookmarkStore.SaveAsync(bookmark, cancellationToken).ConfigureAwait(false);
        await notificationSender.SendAsync(new BookmarkSaved(bookmark), cancellationToken).ConfigureAwait(false);
    }
}