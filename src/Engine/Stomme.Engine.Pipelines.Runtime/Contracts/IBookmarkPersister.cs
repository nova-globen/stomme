using Stomme.Engine.Pipelines.Runtime.Requests;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Persists bookmarks and raises events.
/// </summary>
public interface IBookmarksPersister
{
    /// <summary>
    /// Persists bookmarks and raises events.
    /// </summary>
    Task PersistBookmarksAsync(UpdateBookmarksRequest updateBookmarksRequest);
}