using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Contains extension methods for <see cref="Bookmark"/>.
/// </summary>
public static class BookmarkExtensions
{
    /// <summary>
    /// Converts a <see cref="Bookmark"/> to a <see cref="BookmarkInfo"/>.
    /// </summary>
    public static BookmarkInfo ToBookmarkInfo(this Bookmark source) => new()
    {
        Hash = source.Hash,
        Id = source.Id,
        Name = source.Name
    };
    
    /// <summary>
    /// Converts a collection of <see cref="Bookmark"/> to a collection of <see cref="BookmarkInfo"/>.
    /// </summary>
    public static IEnumerable<BookmarkInfo> ToBookmarkInfos(this IEnumerable<Bookmark> source)
    {
        return source.Select(ToBookmarkInfo);
    }
    
    public static IEnumerable<StoredBookmark> MapBookmarks(this PipelineExecutionContext pipelineExecutionContext, IEnumerable<Bookmark> bookmarks)
    {
        return bookmarks.Select(pipelineExecutionContext.MapBookmark);
    }
    
    public static StoredBookmark MapBookmark(this PipelineExecutionContext pipelineExecutionContext, Bookmark bookmark)
    {
        return new StoredBookmark
        {
            Id = bookmark.Id,
            Name = bookmark.Name,
            Hash = bookmark.Hash,
            PipelineInstanceId = pipelineExecutionContext.Id,
            CreatedAt = bookmark.CreatedAt,
            StepInstanceId = bookmark.StepInstanceId,
            CorrelationId = pipelineExecutionContext.CorrelationId,
            Payload = bookmark.Payload,
            Metadata = bookmark.Metadata
        };
    }
}