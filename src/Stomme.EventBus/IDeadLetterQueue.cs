namespace Stomme.EventBus;

/// <summary>
/// A message that exhausted its delivery attempts and was moved to the dead-letter queue, surfaced for
/// admin inspection.
/// </summary>
/// <param name="Id">Transport identifier used to retry or discard the message.</param>
/// <param name="MessageType">The message's .NET type name, if known.</param>
/// <param name="ErrorDetails">The captured failure details (last error), if any.</param>
/// <param name="EnqueuedUtc">When the message was enqueued. The RavenDB queue fills this from the Rebus SentTime header — the ORIGINAL
/// send time — so it is NOT when the message was dead-lettered; never age dead letters by it (use <paramref name="VisibleUtc"/>).</param>
/// <param name="Headers">The message's transport headers.</param>
/// <param name="VisibleUtc">The time the message became visible in the dead-letter queue (moved there, or re-dead-lettered after a
/// retry); <c>null</c> when the transport cannot say. Appended as a trailing optional parameter so positional construction is unchanged.</param>
public sealed record DeadLetterMessage(
    string Id,
    string? MessageType,
    string? ErrorDetails,
    DateTimeOffset EnqueuedUtc,
    IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset? VisibleUtc = null);

/// <summary>
/// Admin access to the dead-letter queue: list the messages that exhausted retries, retry one (return
/// it to the input queue), or discard it. The implementation is transport-specific; the RavenDB
/// transport backs it with the queue documents.
/// </summary>
public interface IDeadLetterQueue
{
    Task<IReadOnlyList<DeadLetterMessage>> ListAsync(int max = 100, CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the message to the input queue for another delivery attempt. Returns false if it no longer exists.</summary>
    Task<bool> RetryAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Permanently removes the message from the dead-letter queue. Returns false if it no longer exists.</summary>
    Task<bool> DiscardAsync(string id, CancellationToken cancellationToken = default);
}
