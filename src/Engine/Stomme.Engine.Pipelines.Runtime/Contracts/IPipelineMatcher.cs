using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Options;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a contract for finding triggers and bookmarks associated with pipeline steps.
/// </summary>
public interface IPipelineMatcher
{
    /// <summary>
    /// Finds triggers associated with the specified step type and stimulus.
    /// </summary>
    Task<IEnumerable<StoredTrigger>> FindTriggersAsync(string stepTypeName, object stimulus, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds triggers associated with the specified step type and stimulus hash.
    /// </summary>
    Task<IEnumerable<StoredTrigger>> FindTriggersAsync(string stimulusHash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds bookmarks associated with the specified step type and stimulus.
    /// </summary>
    Task<IEnumerable<StoredBookmark>> FindBookmarksAsync(string stepTypeName, object stimulus, FindBookmarkOptions? options = null, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Finds bookmarks associated with the specified step type and stimulus hash.
    /// </summary>
    Task<IEnumerable<StoredBookmark>> FindBookmarksAsync(string stimulusHash, FindBookmarkOptions? options = null, CancellationToken cancellationToken = default);
}