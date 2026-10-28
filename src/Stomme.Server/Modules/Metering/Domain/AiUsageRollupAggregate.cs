#nullable enable
using System;
using System.Security.Cryptography;
using System.Text;
using Stomme.Metering;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Metering.Domain
{
    /// <summary>
    /// A day's AI usage for one slice - site, provider, model, agent - so the dashboard never scans the ledger.
    /// </summary>
    /// <remarks>
    /// The id is DETERMINISTIC over the normalized slice key INCLUDING the site, which is what makes the rollup
    /// job idempotent: a re-run recomputes the same rows rather than appending duplicates. the predecessor platform's job rescanned
    /// the entire ledger every night and never deleted orphans; this one recomputes touched windows and removes
    /// rollups whose ledger rows are gone.
    /// </remarks>
    public sealed class AiUsageRollupAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        // Materialisation constructor. Not for application use.
        private AiUsageRollupAggregate()
        {
            TenantId = null!;
            ProviderNormalized = string.Empty;
            ModelNormalized = string.Empty;
            AgentNormalized = string.Empty;
            CurrencyCode = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        /// <summary>The UTC day this rollup covers, snapped to midnight.</summary>
        public DateTime DayUtc { get; private set; }

        public string ProviderNormalized { get; private set; }

        public string ModelNormalized { get; private set; }

        public string AgentNormalized { get; private set; }

        public long Calls { get; private set; }

        public long InputTokens { get; private set; }

        public long OutputTokens { get; private set; }

        public long Errors { get; private set; }

        public decimal EstimatedCost { get; private set; }

        public string CurrencyCode { get; private set; }

        /// <summary>A METHOD, not a property - the document stores would persist a get-only property.</summary>
        public long BillableTokens() => InputTokens + OutputTokens;

        public static AiUsageRollupAggregate For(TenantId tenantId, int siteId, DateTime dayUtc, string providerNormalized, string modelNormalized, string agentNormalized, string currencyCode)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            var day = new DateTime(dayUtc.Year, dayUtc.Month, dayUtc.Day, 0, 0, 0, DateTimeKind.Utc);
            return new AiUsageRollupAggregate
            {
                Id = IdFor(tenantId, siteId, day, providerNormalized, modelNormalized, agentNormalized),
                TenantId = tenantId,
                SiteId = siteId,
                DayUtc = day,
                ProviderNormalized = providerNormalized,
                ModelNormalized = modelNormalized,
                AgentNormalized = agentNormalized,
                CurrencyCode = string.IsNullOrWhiteSpace(currencyCode) ? "USD" : currencyCode
            };
        }

        /// <summary>Replaces the totals with a freshly recomputed set (the job never accumulates blindly).</summary>
        public void Set(long calls, long inputTokens, long outputTokens, long errors, decimal estimatedCost)
        {
            Calls = calls;
            InputTokens = inputTokens;
            OutputTokens = outputTokens;
            Errors = errors;
            EstimatedCost = estimatedCost;
        }

        /// <summary>The deterministic id for a slice - the same inputs always name the same row.</summary>
        /// <remarks>
        /// The TENANT is part of the key (BL-151). Site ids are not unique across tenants sharing one physical
        /// database, so without it two tenants' rollups for one site would be the same row - a primary-key
        /// collision, or a silent overwrite where the write is GetById-then-Update. This id was missed by the
        /// first pass of that fix and caught by sweeping every deterministic id in the tree afterwards.
        /// </remarks>
        public static Guid IdFor(TenantId tenantId, int siteId, DateTime dayUtc, string providerNormalized, string modelNormalized, string agentNormalized)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            var key = string.Join(
                '|',
                "metering.ai.rollup",
                tenantId.Value.ToString("N"),
                siteId.ToString(),
                dayUtc.ToString("yyyy-MM-dd"),
                MetricKeys.Normalize(providerNormalized),
                MetricKeys.Normalize(modelNormalized),
                MetricKeys.Normalize(agentNormalized));
            return DeterministicId(key);
        }

        /// <summary>A stable GUID from a key - the same shape the quota ids use.</summary>
        internal static Guid DeterministicId(string key)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            var bytes = new byte[16];
            Array.Copy(hash, bytes, 16);
            return new Guid(bytes);
        }
    }
}
