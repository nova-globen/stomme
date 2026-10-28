using System.Globalization;
using Stomme.EventBus;

namespace Stomme.Infrastructure.EventBus;

/// <summary>The outcome of one <see cref="DeadLetterSweeper"/> pass.</summary>
/// <param name="Swept">False when the pass was a no-op (no queryable queue, or a non-positive retention).</param>
/// <param name="Examined">How many dead letters were listed and considered (at most <see cref="DeadLetterSweeper.MaxExaminedPerRun"/>).</param>
/// <param name="Discarded">How many were actually discarded (a message already gone counts as not discarded).</param>
/// <param name="Note">A human-readable line for the job log.</param>
public sealed record DeadLetterSweepResult(bool Swept, int Examined, int Discarded, string Note);

/// <summary>
/// BL-100 O2 (D109) — the pure dead-letter aging logic behind the host's <c>DeadLetterSweepJob</c>: discard every
/// dead letter that became visible in the dead-letter queue before <c>now - retention</c>.
/// </summary>
/// <remarks>
/// <para><b>Aged by <see cref="DeadLetterMessage.VisibleUtc"/>, never <see cref="DeadLetterMessage.EnqueuedUtc"/>.</b>
/// The RavenDB queue fills <c>EnqueuedUtc</c> from the Rebus SentTime header — the ORIGINAL send time — so aging by it
/// would discard a message sent long ago but dead-lettered minutes ago, and a message an admin retried that failed again.
/// <c>VisibleUtc</c> is the time the transport put the message in the error queue, and it is also the queue's list order,
/// so the bounded batch is always the oldest dead letters. A message whose <c>VisibleUtc</c> is <c>null</c> (a transport
/// that cannot say) is NEVER discarded: an unknown age is not an old age.</para>
/// <para><b>A stated no-op without a queryable queue.</b> With <c>Messaging:Enabled</c> false no
/// <see cref="IDeadLetterQueue"/> is registered at all; on every transport other than RavenDB the only one is the
/// <see cref="NullDeadLetterQueue"/> fallback. Both answer a no-op note instead of failing. The queues therefore arrive
/// as an enumerable — the <c>DeadLetterController</c> pattern — because a required resolve would throw.</para>
/// <para><b>Bounded and idempotent.</b> At most <see cref="MaxExaminedPerRun"/> messages are listed per pass. The queue is
/// installation-wide while the job runs once per tenant in a tick, so a second pass finds nothing old left and
/// <see cref="IDeadLetterQueue.DiscardAsync"/> answers false for an id already gone.</para>
/// </remarks>
public static class DeadLetterSweeper
{
    /// <summary>The most dead letters one pass lists and considers — the RavenDB queue's own list clamp.</summary>
    public const int MaxExaminedPerRun = 1000;

    public static async Task<DeadLetterSweepResult> SweepAsync(
        IEnumerable<IDeadLetterQueue> queues,
        DateTimeOffset now,
        TimeSpan retention,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queues);

        var queue = queues.FirstOrDefault();
        if (queue is null || queue is NullDeadLetterQueue)
        {
            return new DeadLetterSweepResult(false, 0, 0,
                "Dead-letter sweep: no queryable dead-letter queue on this transport (a real one exists only when Messaging:Transport=ravendb) - no-op.");
        }

        if (retention <= TimeSpan.Zero)
        {
            return new DeadLetterSweepResult(false, 0, 0,
                "Dead-letter sweep: RetentionDays must be positive; refusing to discard every dead letter - no-op.");
        }

        var cutoff = now - retention;
        var messages = await queue.ListAsync(MaxExaminedPerRun, cancellationToken).ConfigureAwait(false);

        var discarded = 0;
        foreach (var message in messages)
        {
            // Never EnqueuedUtc (the original send time); an unknown visible time is not an old one.
            if (message.VisibleUtc is not { } visibleUtc || visibleUtc >= cutoff)
            {
                continue;
            }

            if (await queue.DiscardAsync(message.Id, cancellationToken).ConfigureAwait(false))
            {
                discarded++;
            }
        }

        return new DeadLetterSweepResult(true, messages.Count, discarded, string.Format(CultureInfo.InvariantCulture,
            "Dead-letter sweep: {0} examined, {1} discarded (visible before {2:u}, retention {3:0} days).",
            messages.Count, discarded, cutoff, retention.TotalDays));
    }
}