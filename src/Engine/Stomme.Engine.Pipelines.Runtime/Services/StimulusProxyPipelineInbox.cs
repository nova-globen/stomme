using Stomme.Engine.Common;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Runtime.Contracts;
using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Messages;
using Stomme.Engine.Pipelines.Runtime.Options;
using Stomme.Engine.Pipelines.Runtime.Params;
using Stomme.Engine.Pipelines.Runtime.Requests;
using Stomme.Engine.Pipelines.Runtime.Results;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// Represents a proxy for sending stimulus to the pipeline runtime while <see cref="IPipelineInbox"/> is being deprecated.
/// </summary>
[Obsolete("Please use the Stimulus API instead")]
public class StimulusProxyPipelineInbox(
    IStimulusSender stimulusSender,
    IPipelineDispatcher pipelineDispatcher,
    ISystemClock systemClock,
    IIdentityGenerator identityGenerator,
    IHasher bookmarkHasher,
    ILogger<StimulusProxyPipelineInbox> logger)
    : IPipelineInbox
{
    /// <inheritdoc />
    public async ValueTask<SubmitPipelineInboxMessageResult> SubmitAsync(NewPipelineInboxMessage message, CancellationToken cancellationToken = default)
    {
        var defaultOptions = new PipelineInboxMessageDeliveryParams();
        return await SubmitAsync(message, defaultOptions, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<SubmitPipelineInboxMessageResult> SubmitAsync(NewPipelineInboxMessage newMessage, PipelineInboxMessageDeliveryParams @params, CancellationToken cancellationToken = default)
    {
        var stepTypeName = newMessage.StepTypeName;
        var stimulus = newMessage.BookmarkPayload;
        var stimulusMetadata = new StimulusMetadata
        {
            CorrelationId = newMessage.CorrelationId,
            PipelineInstanceId = newMessage.PipelineInstanceId,
            StepInstanceId = newMessage.StepInstanceId,
            Input = newMessage.Input
        };

        var now = systemClock.UtcNow;

        var message = new PipelineInboxMessage
        {
            Id = identityGenerator.GenerateId(),
            CreatedAt = now,
            ExpiresAt = now + newMessage.TimeToLive,
            StepInstanceId = newMessage.StepInstanceId,
            CorrelationId = newMessage.CorrelationId,
            PipelineInstanceId = newMessage.PipelineInstanceId,
            StepTypeName = newMessage.StepTypeName,
            BookmarkPayload = newMessage.BookmarkPayload,
            Input = newMessage.Input,
            Hash = bookmarkHasher.Hash(newMessage.StepTypeName, newMessage.BookmarkPayload, newMessage.StepInstanceId),
        };

        var result = await stimulusSender.SendAsync(stepTypeName, stimulus, stimulusMetadata, cancellationToken).ConfigureAwait(false);
        var pipelineExecutionResults = Map(result.PipelineInstanceResponses).ToList();

        return new SubmitPipelineInboxMessageResult(message, pipelineExecutionResults);
    }

    /// <inheritdoc />
    public async ValueTask<DeliverPipelineInboxMessageResult> DeliverAsync(PipelineInboxMessage message, CancellationToken cancellationToken = default)
    {
        await ResumePipelinesAsynchronouslyAsync(message, cancellationToken).ConfigureAwait(false);
        return new DeliverPipelineInboxMessageResult(new List<PipelineExecutionResult>());
    }

    /// <inheritdoc />
    public async ValueTask<DeliverPipelineInboxMessageResult> BroadcastAsync(PipelineInboxMessage message, BroadcastPipelineInboxMessageOptions? options, CancellationToken cancellationToken = default)
    {
        var stepTypeName = message.StepTypeName;
        var correlationId = message.CorrelationId;
        var pipelineInstanceId = message.PipelineInstanceId;
        var stepInstanceId = message.StepInstanceId;
        var bookmarkPayload = message.BookmarkPayload;
        var input = message.Input;

        if (pipelineInstanceId != null)
        {
            if (options?.DispatchAsynchronously == true)
            {
                await ResumePipelinesAsynchronouslyAsync(message, cancellationToken).ConfigureAwait(false);
                return new DeliverPipelineInboxMessageResult(new List<PipelineExecutionResult>());
            }

            var results = await ResumePipelinesSynchronouslyAsync(message, cancellationToken).ConfigureAwait(false);
            return new DeliverPipelineInboxMessageResult(results.ToList());
        }

        if (options?.DispatchAsynchronously == false)
        {
            var result = await stimulusSender.SendAsync(stepTypeName, bookmarkPayload, new StimulusMetadata
            {
                CorrelationId = correlationId,
                PipelineInstanceId = pipelineInstanceId,
                StepInstanceId = stepInstanceId,
                Input = input
            }, cancellationToken).ConfigureAwait(false);

            var pipelineExecutionResults = Map(result.PipelineInstanceResponses).ToList();
            return new DeliverPipelineInboxMessageResult(pipelineExecutionResults);
        }

        var dispatchRequest = new DispatchTriggerPipelinesRequest(stepTypeName, bookmarkPayload)
        {
            CorrelationId = correlationId,
            PipelineInstanceId = pipelineInstanceId,
            StepInstanceId = stepInstanceId,
            Input = input
        };
        await pipelineDispatcher.DispatchAsync(dispatchRequest, cancellationToken).ConfigureAwait(false);
        return new DeliverPipelineInboxMessageResult(new List<PipelineExecutionResult>());
    }

    private async Task ResumePipelinesAsynchronouslyAsync(PipelineInboxMessage message, CancellationToken cancellationToken = default)
    {
        var stepTypeName = message.StepTypeName;
        var correlationId = message.CorrelationId;
        var pipelineInstanceId = message.PipelineInstanceId;
        var stepInstanceId = message.StepInstanceId;
        var bookmarkPayload = message.BookmarkPayload;
        var input = message.Input;

        await pipelineDispatcher.DispatchAsync(new DispatchResumePipelinesRequest(stepTypeName, bookmarkPayload)
        {
            CorrelationId = correlationId,
            PipelineInstanceId = pipelineInstanceId,
            StepInstanceId = stepInstanceId,
            Input = input
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<IEnumerable<PipelineExecutionResult>> ResumePipelinesSynchronouslyAsync(PipelineInboxMessage message, CancellationToken cancellationToken = default)
    {
        var stepTypeName = message.StepTypeName;
        var correlationId = message.CorrelationId;
        var pipelineInstanceId = message.PipelineInstanceId;
        var stepInstanceId = message.StepInstanceId;
        var bookmarkPayload = message.BookmarkPayload;
        var input = message.Input;

        var result = await stimulusSender.SendAsync(stepTypeName, bookmarkPayload, new StimulusMetadata
        {
            CorrelationId = correlationId,
            PipelineInstanceId = pipelineInstanceId,
            StepInstanceId = stepInstanceId,
            Input = input
        }, cancellationToken).ConfigureAwait(false);

        return Map(result.PipelineInstanceResponses).ToList();
    }

    /// <inheritdoc />
    public ValueTask<IEnumerable<PipelineInboxMessage>> FindManyAsync(PipelineInboxMessageFilter filter, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("The pipeline inbox API is deprecated and will be removed in a future version. Please use the stimulus API instead. This method will return an empty list");
        return new([]);
    }

    /// <inheritdoc />
    public ValueTask<IEnumerable<PipelineInboxMessage>> FindManyAsync(IEnumerable<PipelineInboxMessageFilter> filters, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("The pipeline inbox API is deprecated and will be removed in a future version. Please use the stimulus API instead. This method will return an empty list");
        return new([]);
    }

    private static IEnumerable<PipelineExecutionResult> Map(IEnumerable<RunPipelineInstanceResponse> source)
    {
        return source.Select(response => new PipelineExecutionResult(
            response.PipelineInstanceId,
            response.Status,
            response.SubStatus,
            new List<Bookmark>(),
            response.Incidents,
            null,
            new Dictionary<string, object>())
        );
    }
}