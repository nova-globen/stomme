using System;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Metering
{
    /// <summary>
    /// Records consumption of a metered capability.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The port takes no subject, no site and no clock.</b> A recorder that honoured the caller's tenant
    /// verbatim and let the caller name the user and the timestamp would make three things forgeable: the quota
    /// another user is charged against, the site the row lands in, and the window a backdated row falls into. Here
    /// the server derives all three - the user from the request principal, the site from the resolved alias, the
    /// time from the clock - so a producer can only say WHAT was consumed and HOW MUCH.
    /// </para>
    /// <para>
    /// <b>It is idempotent by operation, not atomic.</b> The ledger row and the quota counter are two commits on
    /// every provider (the unit of work persists nothing of its own), so a caller passes a stable
    /// <see cref="UsageEvent.OperationId"/> and a retry appends once. A crash between the two leaves the row
    /// pending and the recovery job folds it; that window is the honest cost of the split.
    /// </para>
    /// </remarks>
    public interface IUsageRecorder
    {
        /// <summary>Records <paramref name="usage"/> for the current user and site.</summary>
        /// <exception cref="MeteringUnavailableException">The metering store could not be reached.</exception>
        Task<UsageOutcome> RecordAsync(UsageEvent usage, CancellationToken ct = default);
    }

    /// <summary>Records one AI call, priced server-side.</summary>
    public interface IAiUsageRecorder
    {
        /// <summary>Records <paramref name="usage"/>; the cost is computed here, never supplied by the caller.</summary>
        /// <exception cref="MeteringUnavailableException">The metering store could not be reached.</exception>
        Task<AiUsageOutcome> RecordAsync(AiUsageEvent usage, CancellationToken ct = default);
    }

    /// <summary>
    /// Answers "may this happen?" BEFORE it happens - the pre-consumption check.
    /// </summary>
    /// <remarks>
    /// It takes no user id: the subject is the caller, always. Asking about somebody else's quota is a reporting
    /// question and goes through the gated queries instead.
    /// </remarks>
    public interface IQuotaInspector
    {
        /// <summary>Whether <paramref name="quantity"/> of <paramref name="metric"/> is within quota right now.</summary>
        Task<QuotaDecision> EvaluateAsync(string metric, long quantity, CancellationToken ct = default);
    }

    /// <summary>What a producer reports: the metric, how much, and which operation it belongs to.</summary>
    /// <param name="Metric">A canonical metric key (see <see cref="MetricKeys"/>).</param>
    /// <param name="Quantity">How many units were consumed; must be positive.</param>
    /// <param name="OperationId">
    /// The operation this usage belongs to. The ledger id is derived from it, so a retry of the SAME operation
    /// appends once. A caller with nothing stable to pass generates one per attempt and gets no idempotency.
    /// </param>
    /// <param name="Source">Optional bounded canonical key naming the producer; free text is rejected.</param>
    public sealed record UsageEvent(string Metric, long Quantity, Guid OperationId, string? Source = null);

    /// <summary>One AI call. Cost is NOT here: it is computed from the provider's price list server-side.</summary>
    public sealed record AiUsageEvent(
        string Provider,
        string Model,
        string Agent,
        long InputTokens,
        long OutputTokens,
        long LatencyMs,
        AiCallStatus Status,
        Guid OperationId,
        double? QualityScore = null);

    /// <summary>The result of recording usage.</summary>
    /// <param name="Recorded">False only when there was no site to record against.</param>
    /// <param name="WithinQuota">
    /// Computed from the state AFTER the fold, so it answers "was this consumption within quota", not "was there
    /// room before it". Over-quota usage is still recorded: the ledger is the record of what happened.
    /// </param>
    /// <param name="Reason">Why the quota answered as it did.</param>
    /// <param name="Idempotent">True when this operation had already been recorded and nothing was appended.</param>
    public sealed record UsageOutcome(bool Recorded, bool WithinQuota, QuotaDecisionReason Reason, bool Idempotent);

    /// <summary>The result of recording an AI call, with the price the server computed.</summary>
    public sealed record AiUsageOutcome(bool Recorded, bool WithinQuota, QuotaDecisionReason Reason, bool Idempotent, decimal EstimatedCost, string CurrencyCode);

    /// <summary>A pre-consumption answer.</summary>
    public sealed record QuotaDecision(bool Allowed, QuotaDecisionReason Reason, long Limit, long Consumed, long Remaining);

    /// <summary>Why a quota decision came out as it did.</summary>
    public enum QuotaDecisionReason
    {
        /// <summary>Within the limit.</summary>
        Within = 0,

        /// <summary>The limit is reached or would be exceeded.</summary>
        Exceeded = 1,

        /// <summary>No limit applies.</summary>
        Unlimited = 2,

        /// <summary>No quota is defined for this metric on this site - permissive, and the usage is still recorded.</summary>
        Undefined = 3,

        /// <summary>The metering store could not be reached; enforcement FAILS CLOSED.</summary>
        Unavailable = 4,

        /// <summary>There is no resolved site to meter against, so nothing was recorded.</summary>
        NoSite = 5
    }

    /// <summary>How an AI call ended.</summary>
    public enum AiCallStatus
    {
        Success = 0,
        Error = 1,
        Cached = 2
    }

    /// <summary>
    /// The metering store could not be reached. NEVER swallowed by the recorder: the producer decides whether its
    /// own operation fails with it, and a silently dropped record is a silently wrong bill.
    /// </summary>
    public sealed class MeteringUnavailableException : Exception
    {
        public MeteringUnavailableException(string message) : base(message)
        {
        }

        public MeteringUnavailableException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
