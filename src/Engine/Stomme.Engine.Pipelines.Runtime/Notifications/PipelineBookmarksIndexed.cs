using Stomme.Engine.Mediator.Contracts;

namespace Stomme.Engine.Pipelines.Runtime.Notifications;

/// <summary>
/// A notification that is sent when the bookmarks of a pipeline instance have been indexed.
/// </summary>
/// <param name="IndexedPipelineBookmarks">The bookmarks that were added, removed, or unchanged.</param>
public record PipelineBookmarksIndexed(IndexedPipelineBookmarks IndexedPipelineBookmarks) : INotification;

