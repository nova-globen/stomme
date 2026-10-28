using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Requests;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class BookmarkUpdater(IBookmarkManager bookmarkManager, IBookmarkStore bookmarkStore) : IBookmarkUpdater
{
    /// <inheritdoc />
    public async Task UpdateBookmarksAsync(UpdateBookmarksRequest request, CancellationToken cancellationToken = default)
    {
        var instanceId = request.PipelineExecutionContext.Id;
        await RemoveBookmarksAsync(instanceId, request.Diff.Removed.ToList(), cancellationToken).ConfigureAwait(false);
        await StoreBookmarksAsync(request.PipelineExecutionContext, request.Diff.Added.ToList(), cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveBookmarksAsync(string pipelineInstanceId, ICollection<Bookmark> bookmarks, CancellationToken cancellationToken)
    {
        if (bookmarks.Count == 0)
            return;

        var matchingIds = bookmarks.Select(x => x.Id).ToList();
        var filter = new BookmarkFilter
        {
            BookmarkIds = matchingIds,
            PipelineInstanceId = pipelineInstanceId
        };
        await bookmarkManager.DeleteManyAsync(filter, cancellationToken).ConfigureAwait(false);
    }

    private async Task StoreBookmarksAsync(PipelineExecutionContext context, ICollection<Bookmark> bookmarks, CancellationToken cancellationToken)
    {
        if (bookmarks.Count == 0)
            return;

        foreach (var bookmark in bookmarks)
        {
            var storedBookmark = context.MapBookmark(bookmark);
            await bookmarkStore.SaveAsync(storedBookmark, cancellationToken).ConfigureAwait(false);
        }
    }
}