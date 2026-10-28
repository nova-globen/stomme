using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Manages bookmarks.
/// </summary>
public interface IBookmarkManager
{
    /// <summary>
    /// Deletes all bookmarks matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>The number of deleted bookmarks.</returns>
    Task<long> DeleteManyAsync(BookmarkFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves the specified bookmark and raises a notification that the bookmark has been saved.
    /// </summary>
    Task SaveAsync(StoredBookmark bookmark, CancellationToken cancellationToken = default);
}