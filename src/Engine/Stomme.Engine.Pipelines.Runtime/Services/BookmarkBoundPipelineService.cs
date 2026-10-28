using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Options;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
[Obsolete("Will be removed in a future version.")]
public class BookmarkBoundPipelineService(IPipelineMatcher pipelineMatcher) : IBookmarkBoundPipelineService
{
    /// <inheritdoc />
    public async Task<IEnumerable<BookmarkBoundPipeline>> FindManyAsync(string stepTypeName, object stimulus, FindBookmarkOptions? options = null, CancellationToken cancellationToken = default)
    {
        var bookmarks = await pipelineMatcher.FindBookmarksAsync(stepTypeName, stimulus, options, cancellationToken).ConfigureAwait(false);
        return Map(bookmarks);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<BookmarkBoundPipeline>> FindManyAsync(string stimulusHash, FindBookmarkOptions? options = null, CancellationToken cancellationToken = default)
    {
        var bookmarks = await pipelineMatcher.FindBookmarksAsync(stimulusHash, options, cancellationToken).ConfigureAwait(false);
        return Map(bookmarks);
    }

    private IEnumerable<BookmarkBoundPipeline> Map(IEnumerable<StoredBookmark> bookmarks)
    {
        var groupedBookmarks = bookmarks.GroupBy(x => x.PipelineInstanceId);
        var bookmarkBoundPipelines = new List<BookmarkBoundPipeline>();

        foreach (var bookmarkGroup in groupedBookmarks)
        {
            var pipelineInstanceId = bookmarkGroup.Key;
            var bookmarkBoundPipeline = new BookmarkBoundPipeline(pipelineInstanceId, bookmarkGroup.ToList());
            bookmarkBoundPipelines.Add(bookmarkBoundPipeline);
        }

        return bookmarkBoundPipelines;
    }
}