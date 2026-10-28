using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// Contains the bookmarks that were added, removed, or unchanged.
/// </summary>
/// <param name="PipelineExecutionContext">The pipeline execution context.</param>
/// <param name="AddedBookmarks">The bookmarks that were added.</param>
/// <param name="RemovedBookmarks">The bookmarks that were removed.</param>
/// <param name="UnchangedBookmarks">The bookmarks that were unchanged.</param>
public record IndexedPipelineBookmarks(
    PipelineExecutionContext PipelineExecutionContext,
    ICollection<Bookmark> AddedBookmarks,
    ICollection<Bookmark> RemovedBookmarks,
    ICollection<Bookmark> UnchangedBookmarks);