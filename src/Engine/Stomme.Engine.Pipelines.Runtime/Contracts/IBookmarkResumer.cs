using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Options;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Resumes pipelines using a given stimulus or bookmark filter.
/// </summary>
[Obsolete("Use IPipelineResumer instead.")]
public interface IBookmarkResumer
{
    /// <summary>
    /// Resumes the bookmark.
    /// </summary>
    Task<ResumeBookmarkResult> ResumeAsync<TStep>(object stimulus, ResumeBookmarkOptions? options = null, CancellationToken cancellationToken = default) where TStep : IStep;

    /// <summary>
    /// Resumes the bookmark.
    /// </summary>
    Task<ResumeBookmarkResult> ResumeAsync(string bookmarkId, IDictionary<string, object> input, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Resumes the bookmark.
    /// </summary>
    Task<ResumeBookmarkResult> ResumeAsync<TStep>(object stimulus, string? pipelineInstanceId, ResumeBookmarkOptions? options = null, CancellationToken cancellationToken = default) where TStep : IStep;
    
    /// <summary>
    /// Resumes the bookmark.
    /// </summary>
    Task<ResumeBookmarkResult> ResumeAsync<TStep>(string bookmarkId, ResumeBookmarkOptions? options = null, CancellationToken cancellationToken = default) where TStep : IStep;

    /// Resumes the bookmark.
    Task<ResumeBookmarkResult> ResumeAsync(ResumeBookmarkRequest request, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Resumes the bookmark.
    /// </summary>
    Task<ResumeBookmarkResult> ResumeAsync(BookmarkFilter filter, ResumeBookmarkOptions? options = null, CancellationToken cancellationToken = default);
}