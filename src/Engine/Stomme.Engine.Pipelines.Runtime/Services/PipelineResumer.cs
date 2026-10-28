using Stomme.Engine.Common.DistributedHosting;
using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Runtime.Exceptions;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Messages;
using Stomme.Engine.Pipelines.Runtime.Options;
using Medallion.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class PipelineResumer(
    IPipelineRuntime pipelineRuntime,
    IBookmarkStore bookmarkStore,
    IStimulusHasher stimulusHasher,
    IDistributedLockProvider distributedLockProvider,
    IOptions<DistributedLockingOptions> distributedLockingOptions,
    ILogger<PipelineResumer> logger) : IPipelineResumer
{
    /// <inheritdoc />
    public Task<IEnumerable<RunPipelineInstanceResponse>> ResumeAsync<TStep>(object stimulus, ResumeBookmarkOptions? options = null, CancellationToken cancellationToken = default) where TStep : IStep
    {
        return ResumeAsync<TStep>(stimulus, null, options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RunPipelineInstanceResponse>> ResumeAsync<TStep>(object stimulus, string? pipelineInstanceId = null, ResumeBookmarkOptions? options = null, CancellationToken cancellationToken = default) where TStep : IStep
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
    public async Task<RunPipelineInstanceResponse?> ResumeAsync(string bookmarkId, IDictionary<string, object> input, CancellationToken cancellationToken = default)
    {
        var bookmarkFilter = new BookmarkFilter
        {
            BookmarkId = bookmarkId
        };
        var options = new ResumeBookmarkOptions
        {
            Input = input
        };
        var responses = await ResumeAsync(bookmarkFilter, options, cancellationToken).ConfigureAwait(false);
        return responses.FirstOrDefault();
    }

    /// <inheritdoc />
    public async Task<RunPipelineInstanceResponse?> ResumeAsync<TStep>(string bookmarkId, ResumeBookmarkOptions? options = null, CancellationToken cancellationToken = default) where TStep : IStep
    {
        var stepTypeName = StepTypeNameHelper.GenerateTypeName<TStep>();
        var bookmarkFilter = new BookmarkFilter
        {
            Name = stepTypeName,
            BookmarkId = bookmarkId
        };
        var response = await ResumeAsync(bookmarkFilter, options, cancellationToken).ConfigureAwait(false);
        return response.FirstOrDefault();
    }

    public async Task<IEnumerable<RunPipelineInstanceResponse>> ResumeAsync(ResumeBookmarkRequest request, CancellationToken cancellationToken = default)
    {
        var filter = new BookmarkFilter
        {
            BookmarkId = request.BookmarkId,
            StepInstanceId = request.StepInstanceId ?? request.StepHandle?.StepInstanceId,
        };

        var resumeOptions = new ResumeBookmarkOptions()
        {
            Input = request.Input,
            Properties = request.Properties,
        };
        return await ResumeAsync(filter, resumeOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<RunPipelineInstanceResponse>> ResumeAsync(BookmarkFilter filter, ResumeBookmarkOptions? options = null, CancellationToken cancellationToken = default)
    {
        var hashableFilterString = filter.GetHashableString();
        var lockKey = $"pipeline-resumer:{hashableFilterString}";

        try
        {
            var filterLock = (await distributedLockProvider.AcquireLockAsync(lockKey, distributedLockingOptions.Value.LockAcquisitionTimeout, cancellationToken).ConfigureAwait(false));
            await using var _filterLockAsyncDisposal = filterLock.ConfigureAwait(false);
            var bookmarks = (await bookmarkStore.FindManyAsync(filter, cancellationToken).ConfigureAwait(false)).ToList();

            if (bookmarks.Count == 0)
            {
                logger.LogDebug("No bookmarks found in store for filter {@Filter}", filter);
                return [];
            }

            var responses = new List<RunPipelineInstanceResponse>();
            foreach (var bookmark in bookmarks)
            {
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
                    responses.Add(response);
                }
                catch (PipelineInstanceNotFoundException)
                {
                    // The pipeline instance does not (yet) exist in the DB.
                    logger.LogDebug("No pipeline instance with ID {PipelineInstanceId} found for bookmark {BookmarkId} at this time.", bookmark.PipelineInstanceId, bookmark.Id);
                }
            }

            return responses;
        }
        catch (TimeoutException e)
        {
            // Rethrow but with a more specific message.
            throw new TimeoutException($"Could not acquire distributed lock with key '{lockKey}' within the configured timeout of {distributedLockingOptions.Value.LockAcquisitionTimeout}.", e);
        }
    }
}