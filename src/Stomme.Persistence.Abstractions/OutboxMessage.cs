namespace Stomme.Persistence.Abstractions;

/// <summary>
/// A domain event captured in the transactional outbox: written to the store in the same transaction
/// as the aggregate that raised it, then dispatched asynchronously by the outbox processor. This
/// turns event delivery from at-most-once (in-process dispatch) into at-least-once (durable, retried
/// until processed), so handlers must be idempotent.
/// </summary>
public sealed class OutboxMessage
{
    /// <summary>Unique id of the captured message.</summary>
    public Guid Id { get; set; }

    /// <summary>Registry key identifying the domain event CLR type (its full name).</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>Serialized event payload (JSON).</summary>
    public string Payload { get; set; } = string.Empty;

    /// <summary>Tenant under which the event was raised; null means system scope.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>When the event occurred.</summary>
    public DateTimeOffset OccurredOnUtc { get; set; }

    /// <summary>When the message was successfully dispatched; null while pending.</summary>
    public DateTimeOffset? ProcessedOnUtc { get; set; }

    /// <summary>Number of failed dispatch attempts.</summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Earliest time the message may be retried after a failure (set by the processor from the
    /// exponential backoff schedule); null means it is immediately eligible.
    /// </summary>
    public DateTimeOffset? NextAttemptOnUtc { get; set; }

    /// <summary>The last dispatch error, if any.</summary>
    public string? LastError { get; set; }
}
