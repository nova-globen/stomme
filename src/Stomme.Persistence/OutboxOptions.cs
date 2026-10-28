namespace Stomme.Persistence;

/// <summary>Tuning for the outbox processor.</summary>
public sealed class OutboxOptions
{
    /// <summary>How often the background processor polls for pending messages.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum messages dispatched per poll.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>Lease duration for the cross-node processing lock (when a distributed lock is available).</summary>
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Maximum dispatch attempts before a message is left as a dead letter (no longer retried). The
    /// row stays in the outbox with its last error for inspection.
    /// </summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>
    /// Base delay before a failed message is retried. Each successive failure doubles the delay
    /// (exponential backoff), capped at <see cref="MaxRetryBackoff"/>. Set to <see cref="TimeSpan.Zero"/>
    /// to retry on the next poll with no backoff.
    /// </summary>
    public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound on the exponential retry delay.</summary>
    public TimeSpan MaxRetryBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The delay before a message that has already failed <paramref name="priorFailures"/> times may
    /// be retried: <c>RetryBackoff * 2^priorFailures</c>, capped at <see cref="MaxRetryBackoff"/>.
    /// Returns <see cref="TimeSpan.Zero"/> when backoff is disabled or the count is non-positive.
    /// </summary>
    public TimeSpan BackoffFor(int priorFailures)
    {
        if (RetryBackoff <= TimeSpan.Zero || priorFailures <= 0)
        {
            return RetryBackoff <= TimeSpan.Zero ? TimeSpan.Zero : RetryBackoff;
        }

        // Cap the exponent before the multiply so the double never overflows on a runaway attempt count.
        var exponent = Math.Min(priorFailures, 30);
        var delayMs = RetryBackoff.TotalMilliseconds * Math.Pow(2, exponent);
        var cappedMs = Math.Min(delayMs, MaxRetryBackoff.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(cappedMs);
    }
}
