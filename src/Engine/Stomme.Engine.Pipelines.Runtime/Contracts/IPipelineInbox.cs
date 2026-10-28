using Stomme.Engine.Pipelines.Runtime.Entities;
using Stomme.Engine.Pipelines.Runtime.Filters;
using Stomme.Engine.Pipelines.Runtime.Options;
using Stomme.Engine.Pipelines.Runtime.Params;
using Stomme.Engine.Pipelines.Runtime.Results;

namespace Stomme.Engine.Pipelines.Runtime;

/// <summary>
/// An inbox for delivering messages to pipeline instances.
/// </summary>
[Obsolete("Use the Stimulus API instead")]
public interface IPipelineInbox
{
    /// <summary>
    /// Stores a message for a pipeline instance.
    /// </summary>
    /// <param name="message">The message to deliver.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    ValueTask<SubmitPipelineInboxMessageResult> SubmitAsync(NewPipelineInboxMessage message, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Stores a message for a pipeline instance.
    /// </summary>
    /// <param name="message">The message to store.</param>
    /// <param name="options">An optional set of delivery options.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    ValueTask<SubmitPipelineInboxMessageResult> SubmitAsync(NewPipelineInboxMessage message, PipelineInboxMessageDeliveryParams options, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Delivers a message to a pipeline instance.
    /// </summary>
    /// <param name="message">The message to deliver.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    ValueTask<DeliverPipelineInboxMessageResult> DeliverAsync(PipelineInboxMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Broadcasts the specified message, which may trigger new pipelines and resume existing ones.
    /// </summary>
    /// <param name="message">The message to broadcast.</param>
    /// <param name="options">An optional set of delivery options.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    ValueTask<DeliverPipelineInboxMessageResult> BroadcastAsync(PipelineInboxMessage message, BroadcastPipelineInboxMessageOptions? options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds all messages matching the specified filter.
    /// </summary>
    /// <param name="filter">The filter to apply.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>A list of messages that match the filter.</returns>
    ValueTask<IEnumerable<PipelineInboxMessage>> FindManyAsync(PipelineInboxMessageFilter filter, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Finds all messages matching the specified filter.
    /// </summary>
    /// <param name="filters">The filters to apply.</param>
    /// <param name="cancellationToken">An optional cancellation token.</param>
    /// <returns>A list of messages that match the filter.</returns>
    ValueTask<IEnumerable<PipelineInboxMessage>> FindManyAsync(IEnumerable<PipelineInboxMessageFilter> filters, CancellationToken cancellationToken = default);
}