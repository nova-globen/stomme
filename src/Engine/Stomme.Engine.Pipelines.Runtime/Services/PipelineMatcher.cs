using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Extensions;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Options;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class PipelineMatcher(IStimulusHasher stimulusHasher, ITriggerStore triggerStore, IBookmarkStore bookmarkStore) : IPipelineMatcher
{
    /// <inheritdoc />
    public Task<IEnumerable<StoredTrigger>> FindTriggersAsync(string stepTypeName, object stimulus, CancellationToken cancellationToken = default)
    {
        var hash = stimulusHasher.Hash(stepTypeName, stimulus);
        return FindTriggersAsync(hash, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<StoredTrigger>> FindTriggersAsync(string stimulusHash, CancellationToken cancellationToken = default)
    {
        return await triggerStore.FindManyByStimulusHashAsync(stimulusHash, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<IEnumerable<StoredBookmark>> FindBookmarksAsync(string stepTypeName, object stimulus, FindBookmarkOptions? options = null, CancellationToken cancellationToken = default)
    {
        var hash = stimulusHasher.Hash(stepTypeName, stimulus, options?.StepInstanceId);
        return FindBookmarksAsync(hash, options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<StoredBookmark>> FindBookmarksAsync(string stimulusHash, FindBookmarkOptions? options = null, CancellationToken cancellationToken = default)
    {
        var correlationId = options?.CorrelationId;
        var pipelineInstanceId = options?.PipelineInstanceId;
        var stepInstanceId = options?.StepInstanceId;
        var filter = new BookmarkFilter
        {
            Hash = stimulusHash,
            CorrelationId = correlationId,
            PipelineInstanceId = pipelineInstanceId,
            StepInstanceId = stepInstanceId,
            BookmarkId = options?.BookmarkId
        };
        return await bookmarkStore.FindManyAsync(filter, cancellationToken).ConfigureAwait(false);
    }
}