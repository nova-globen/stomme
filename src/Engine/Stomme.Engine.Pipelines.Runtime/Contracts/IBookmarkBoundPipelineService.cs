using Stomme.Engine.Pipelines.Runtime.Options;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a service that looks up bookmark-bound pipelines.
/// </summary>
[Obsolete("Will be removed in a future version.")]
public interface IBookmarkBoundPipelineService
{
    /// <summary>
    /// Finds bookmark-bound pipelines by step type name and stimulus.
    /// </summary>
    Task<IEnumerable<BookmarkBoundPipeline>> FindManyAsync(string stepTypeName, object stimulus, FindBookmarkOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds bookmark-bound pipelines by stimulus hash.
    /// </summary>
    Task<IEnumerable<BookmarkBoundPipeline>> FindManyAsync(string stimulusHash, FindBookmarkOptions? options = null, CancellationToken cancellationToken = default);
}