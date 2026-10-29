#nullable enable
using System;
using Stomme.Metering;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Metering.Domain
{
    /// <summary>
    /// One AI call: which provider and model, how many tokens, what it cost, how it ended.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No prompt, no completion, ever.</b> the predecessor platform's record carried none either, and that holds here: what is
    /// kept is the shape of the call, not its content.
    /// </para>
    /// <para>
    /// <b>Provider, model and agent are D49 PAIRS.</b> The raw value keeps the vendor's own spelling, because the
    /// dashboard renders it; the normalized twin is what every filter compares, because bare string equality
    /// diverges across providers. Lower-casing in place - the shape the design first proposed - would have made
    /// the two providers agree by destroying the thing the UI displays.
    /// </para>
    /// <para>
    /// The cost is computed server-side from a price list and stored as <see cref="decimal"/> with its currency;
    /// <see cref="decimal"/> is deliberately never used in a predicate, since it sits outside the FindAsync
    /// contract's safe set.
    /// </para>
    /// </remarks>
    public sealed class AiUsageRecordAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        // Materialisation constructor. Not for application use.
        private AiUsageRecordAggregate()
        {
            TenantId = null!;
            Provider = string.Empty;
            ProviderNormalized = string.Empty;
            Model = string.Empty;
            ModelNormalized = string.Empty;
            Agent = string.Empty;
            AgentNormalized = string.Empty;
            CurrencyCode = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        /// <summary>The vendor's own spelling, trimmed - what the dashboard renders.</summary>
        public string Provider { get; private set; }

        /// <summary>The D49 normalized twin - what every filter compares.</summary>
        public string ProviderNormalized { get; private set; }

        public string Model { get; private set; }

        public string ModelNormalized { get; private set; }

        /// <summary>Which agent made the call (a lifted vocabulary; a string, not an enum naming later waves).</summary>
        public string Agent { get; private set; }

        public string AgentNormalized { get; private set; }

        public long InputTokens { get; private set; }

        public long OutputTokens { get; private set; }

        public long LatencyMs { get; private set; }

        public AiCallStatus Status { get; private set; }

        public decimal EstimatedCost { get; private set; }

        public string CurrencyCode { get; private set; }

        public double? QualityScore { get; private set; }

        public int? UserId { get; private set; }

        public Guid OperationId { get; private set; }

        public DateTime OccurredOnUtc { get; private set; }

        /// <summary>A METHOD, not a property: a get-only property becomes a stored field on the document stores.</summary>
        /// <summary>
        /// The tokens this call is CHARGED for: none for a cache hit. A method, not a property - see the type remarks.
        /// </summary>
        /// <remarks>
        /// A cached answer cost the provider nothing, so it costs the site nothing: no price, and no tokens against a
        /// token quota. The raw counts are kept - they say how much the cache saved. the predecessor platform had this rule and the lift
        /// dropped it without a word (found 2026-09-21 by reconciling its tests by name); it lives HERE so that no caller
        /// can store a priced cache hit.
        /// </remarks>
        public long BillableTokens() => Status == AiCallStatus.Cached ? 0 : InputTokens + OutputTokens;

        public static AiUsageRecordAggregate Append(
            Guid id,
            TenantId tenantId,
            int siteId,
            string provider,
            string model,
            string agent,
            long inputTokens,
            long outputTokens,
            long latencyMs,
            AiCallStatus status,
            decimal estimatedCost,
            string currencyCode,
            double? qualityScore,
            int? userId,
            Guid operationId,
            DateTime occurredOnUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            ArgumentException.ThrowIfNullOrWhiteSpace(provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId));
            }

            if (inputTokens < 0 || outputTokens < 0 || latencyMs < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(inputTokens), "Token counts and latency are not negative.");
            }

            return new AiUsageRecordAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                Provider = provider.Trim(),
                ProviderNormalized = MetricKeys.Normalize(provider),
                Model = model.Trim(),
                ModelNormalized = MetricKeys.Normalize(model),
                Agent = (agent ?? string.Empty).Trim(),
                AgentNormalized = MetricKeys.Normalize(agent),
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                LatencyMs = latencyMs,
                Status = status,
                EstimatedCost = status == AiCallStatus.Cached ? 0m : estimatedCost,
                CurrencyCode = string.IsNullOrWhiteSpace(currencyCode) ? "USD" : currencyCode.Trim().ToUpperInvariant(),
                QualityScore = qualityScore,
                UserId = userId,
                OperationId = operationId,
                OccurredOnUtc = occurredOnUtc
            };
        }

        /// <summary>Pseudonymises the row on user deletion.</summary>
        public void ForgetUser() => UserId = null;
    }
}
