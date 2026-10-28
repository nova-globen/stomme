using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a pipeline bound to one or more bookmarks.
/// </summary>
public record BookmarkBoundPipeline(string PipelineInstanceId, ICollection<StoredBookmark> Bookmarks);