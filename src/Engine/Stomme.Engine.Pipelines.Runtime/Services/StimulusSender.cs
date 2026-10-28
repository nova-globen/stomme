using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Messages;
using Stomme.Engine.Pipelines.Runtime.Results;
using Microsoft.Extensions.Logging;
using Open.Linq.AsyncExtensions;

namespace Stomme.Engine.Pipelines.Runtime;

/// <inheritdoc />
public class StimulusSender(
    IStimulusHasher stimulusHasher,
    ITriggerBoundPipelineService triggerBoundPipelineService,
    IPipelineResumer pipelineResumer,
    IBookmarkQueue bookmarkQueue,
    ITriggerInvoker triggerInvoker,
    ILogger<StimulusSender> logger) : IStimulusSender
{
    /// <inheritdoc />
    public Task<SendStimulusResult> SendAsync(string stepTypeName, object stimulus, StimulusMetadata? metadata = null, CancellationToken cancellationToken = default)
    {
        var stimulusHash = stimulusHasher.Hash(stepTypeName, stimulus, metadata?.StepInstanceId);
        return SendAsync(stimulusHash, metadata, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SendStimulusResult> SendAsync(string stimulusHash, StimulusMetadata? metadata = null, CancellationToken cancellationToken = default)
    {
        var responses = new List<RunPipelineInstanceResponse>();

        if (metadata == null || (metadata.PipelineInstanceId == null && metadata.BookmarkId == null && metadata.StepInstanceId == null))
        {
            var triggered = await TriggerNewPipelinesAsync(stimulusHash, metadata, cancellationToken).ConfigureAwait(false);
            responses.AddRange(triggered);
        }

        var resumed = await ResumeExistingPipelinesAsync(stimulusHash, metadata, cancellationToken).ConfigureAwait(false);
        responses.AddRange(resumed);
        return new(responses);
    }

    private async Task<ICollection<RunPipelineInstanceResponse>> TriggerNewPipelinesAsync(string stimulusHash, StimulusMetadata? metadata = null, CancellationToken cancellationToken = default)
    {
        var triggerBoundPipelines = await triggerBoundPipelineService.FindManyAsync(stimulusHash, cancellationToken).ToList().ConfigureAwait(false);
        var correlationId = metadata?.CorrelationId;
        var input = metadata?.Input;
        var properties = metadata?.Properties;
        var parentId = metadata?.ParentPipelineInstanceId;
        var responses = new List<RunPipelineInstanceResponse>();

        foreach (var triggerBoundPipeline in triggerBoundPipelines)
        {
            var pipelineGraph = triggerBoundPipeline.PipelineGraph;
            var pipeline = pipelineGraph.Pipeline;

            foreach (var trigger in triggerBoundPipeline.Triggers)
            {
                var triggerRequest = new InvokeTriggerRequest
                {
                    CorrelationId = correlationId,
                    Pipeline = pipeline,
                    StepId = trigger.StepId,
                    Input = input,
                    Properties = properties,
                    ParentPipelineInstanceId = parentId
                };

                var response = await triggerInvoker.InvokeAsync(triggerRequest, cancellationToken).ConfigureAwait(false);

                if (response.CannotStart)
                {
                    logger.LogWarning("Pipeline activation strategy disallowed starting pipeline {PipelineDefinitionHandle} with correlation ID {CorrelationId}", pipeline.DefinitionHandle, correlationId);
                    continue;
                }

                responses.Add(response.ToRunPipelineInstanceResponse());
            }
        }

        return responses;
    }

    private async Task<ICollection<RunPipelineInstanceResponse>> ResumeExistingPipelinesAsync(string stimulusHash, StimulusMetadata? metadata, CancellationToken cancellationToken)
    {
        var input = metadata?.Input;
        var properties = metadata?.Properties;
        
        var bookmarkFilter = new BookmarkFilter
        {
            Hash = stimulusHash,
            CorrelationId = metadata?.CorrelationId,
            PipelineInstanceId = metadata?.PipelineInstanceId,
            StepInstanceId = metadata?.StepInstanceId,
            BookmarkId = metadata?.BookmarkId
        };
        var responses = (await pipelineResumer.ResumeAsync(bookmarkFilter, new()
        {
            Input = input,
            Properties = properties
        }, cancellationToken).ConfigureAwait(false)).ToList();

        if (responses.Count > 0)
        {
            logger.LogDebug("Successfully resumed {PipelineCount} pipeline instances using stimulus {StimulusHash}", responses.Count, stimulusHash);
            return responses;
        }

        // If no bookmarks were matched, enqueue the request in case a matching bookmark is created in the near future.
        var pipelineInstanceId = metadata?.PipelineInstanceId;

        var bookmarkQueueItem = new NewBookmarkQueueItem
        {
            PipelineInstanceId = pipelineInstanceId,
            BookmarkId = metadata?.BookmarkId,
            CorrelationId = metadata?.CorrelationId,
            StimulusHash = stimulusHash,
            Options = new()
            {
                Input = input,
                Properties = properties
            }
        };
            
        logger.LogDebug("Bookmark queue item enqueued with stimulus: {StimulusHash}", bookmarkQueueItem.StimulusHash);
            
        await bookmarkQueue.EnqueueAsync(bookmarkQueueItem, cancellationToken).ConfigureAwait(false);

        return responses;
    }
}