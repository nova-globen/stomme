using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Messages;
using Stomme.Engine.Pipelines.Runtime.Options;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Resumes pipelines using a given stimulus or bookmark filter.
/// </summary>
public interface IPipelineResumer
{
    /// <summary>
    /// Resumes the pipelines associated with the bookmarks matching the given stimulus.
    /// </summary>
    Task<IEnumerable<RunPipelineInstanceResponse>> ResumeAsync<TStep>(object stimulus, ResumeBookmarkOptions? options = null, CancellationToken cancellationToken = default) where TStep : IStep;

    /// <summary>
    /// Resumes the pipeline associated with the bookmark specified by the given bookmark ID.
    /// </summary>
    Task<RunPipelineInstanceResponse?> ResumeAsync(string bookmarkId, IDictionary<string, object> input, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Resumes the pipelines associated with the bookmarks matching the given stimulus. If a pipeline instance ID is specified, only resumes pipelines associated with that instance.
    /// </summary>
    Task<IEnumerable<RunPipelineInstanceResponse>> ResumeAsync<TStep>(object stimulus, string? pipelineInstanceId, ResumeBookmarkOptions? options = null, CancellationToken cancellationToken = default) where TStep : IStep;
    
    /// <summary>
    /// Resumes the pipeline associated with the bookmark specified by the given bookmark ID.
    /// </summary>
    Task<RunPipelineInstanceResponse?> ResumeAsync<TStep>(string bookmarkId, ResumeBookmarkOptions? options = null, CancellationToken cancellationToken = default) where TStep : IStep;

    /// Resumes the pipelines associated with the bookmarks matching the given request.
    Task<IEnumerable<RunPipelineInstanceResponse>> ResumeAsync(ResumeBookmarkRequest request, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Resumes the pipelines matching the given bookmark filter.
    /// </summary>
    Task<IEnumerable<RunPipelineInstanceResponse>> ResumeAsync(BookmarkFilter filter, ResumeBookmarkOptions? options = null, CancellationToken cancellationToken = default);
}