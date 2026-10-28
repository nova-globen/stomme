using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Runtime.Exceptions;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Messages;
using Stomme.Engine.Pipelines.Runtime.Options;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
[Obsolete("Use PipelineResumer instead.")]
public class BookmarkResumer(IPipelineRuntime pipelineRuntime, IBookmarkStore bookmarkStore, IStimulusHasher stimulusHasher, ILogger<BookmarkResumer> logger) : IBookmarkResumer
{
    /// <inheritdoc />
    public Task<ResumeBookmarkResult> ResumeAsync<TStep>(object stimulus, ResumeBookmarkOptions? options = null, CancellationToken cancellationToken = default) where TStep : IStep
    {
        return ResumeAsync<TStep>(stimulus, null, options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ResumeBookmarkResult> ResumeAsync<TStep>(object stimulus, string? pipelineInstanceId = null, ResumeBookmarkOptions? options = null, CancellationToken cancellationToken = default) where TStep : IStep
    {
        var stepTypeName = StepTypeNameHelper.GenerateTypeName<TStep>();
        var stimulusHash = stimulusHasher.Hash(stepTypeName, stimulus);
        var bookmarkFilter = new BookmarkFilter
        {
            Name = stepTypeName,
            PipelineInstanceId = pipelineInstanceId,
            Hash = stimulusHash,
        };
        return await ResumeAsync(bookmarkFilter, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ResumeBookmarkResult> ResumeAsync(string bookmarkId, IDictionary<string, object> input, CancellationToken cancellationToken = default)
    {
        var bookmarkFilter = new BookmarkFilter
        {
            BookmarkId = bookmarkId
        };
        var options = new ResumeBookmarkOptions
        {
            Input = input
        };
        return await ResumeAsync(bookmarkFilter, options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ResumeBookmarkResult> ResumeAsync<TStep>(string bookmarkId, ResumeBookmarkOptions? options = null, CancellationToken cancellationToken = default) where TStep : IStep
    {
        var stepTypeName = StepTypeNameHelper.GenerateTypeName<TStep>();
        var bookmarkFilter = new BookmarkFilter
        {
            Name = stepTypeName,
            BookmarkId = bookmarkId
        };
        return await ResumeAsync(bookmarkFilter, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ResumeBookmarkResult> ResumeAsync(ResumeBookmarkRequest request, CancellationToken cancellationToken = default)
    {
        var runRequest = new RunPipelineInstanceRequest
        {
            Input = request.Input,
            Properties = request.Properties,
            StepHandle = request.StepHandle,
            BookmarkId = request.BookmarkId
        };

        var pipelineInstanceId = request.PipelineInstanceId;
        var pipelineClient = await pipelineRuntime.CreateClientAsync(pipelineInstanceId, cancellationToken).ConfigureAwait(false);
        var response = await pipelineClient.RunInstanceAsync(runRequest, cancellationToken).ConfigureAwait(false);
        return ResumeBookmarkResult.Found(response);
    }

    /// <inheritdoc />
    public async Task<ResumeBookmarkResult> ResumeAsync(BookmarkFilter filter, ResumeBookmarkOptions? options = null, CancellationToken cancellationToken = default)
    {
        var bookmark = await bookmarkStore.FindAsync(filter, cancellationToken).ConfigureAwait(false);

        if (bookmark == null)
        {
            logger.LogDebug("Bookmark not found in store for filter {@Filter}", filter);
            return ResumeBookmarkResult.NotFound();
        }

        var pipelineClient = await pipelineRuntime.CreateClientAsync(bookmark.PipelineInstanceId, cancellationToken).ConfigureAwait(false);
        var runRequest = new RunPipelineInstanceRequest
        {
            Input = options?.Input,
            Properties = options?.Properties,
            BookmarkId = bookmark.Id
        };

        try
        {
            var response = await pipelineClient.RunInstanceAsync(runRequest, cancellationToken).ConfigureAwait(false);
            logger.LogDebug("Resumed pipeline instance {PipelineInstanceId} with bookmark {BookmarkId}", bookmark.PipelineInstanceId, bookmark.Id);
            return ResumeBookmarkResult.Found(response);
        }
        catch (PipelineInstanceNotFoundException)
        {
            // The pipeline instance does not (yet) exist in the DB.
            logger.LogDebug("No pipeline instance with ID {PipelineInstanceId} found for bookmark {BookmarkId} at this time.", bookmark.PipelineInstanceId, bookmark.Id);
            return ResumeBookmarkResult.NotFound();
        }
    }
}