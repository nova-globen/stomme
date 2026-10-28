#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Metering;
using Stomme.Modules.Metering.Domain;
using Stomme.Persistence.Abstractions;
using Stomme.Security.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Metering.Application
{
    /// <summary>
    /// Records one AI call: the ledger row, its server-computed price, and the shared metric it consumes (D102).
    /// </summary>
    /// <remarks>
    /// AiUsage is NOT a second module. the predecessor platform ran two of them and bridged one into the other in the host's
    /// composition root; here the AI recorder simply calls the usage recorder in-process for the metric side, so
    /// there is one quota engine, one ledger convention and no host bridge to forget.
    /// </remarks>
    public sealed class AiUsageRecorder : IAiUsageRecorder
    {
        /// <summary>The metric AI calls consume; a site quota on this key limits AI usage.</summary>
        public const string CallsMetric = "ai.calls";

        /// <summary>The metric AI tokens consume.</summary>
        public const string TokensMetric = "ai.tokens";

        private readonly IServiceScopeFactory _scopes;
        private readonly MeteringScope _scope;
        private readonly IMeteringClock _clock;
        private readonly AiCostCalculator _prices;
        private readonly IUsageRecorder _usage;
        private readonly ITenantScopeFactory _tenantScopes;

        public AiUsageRecorder(
            IServiceScopeFactory scopes,
            MeteringScope scope,
            IMeteringClock clock,
            AiCostCalculator prices,
            IUsageRecorder usage,
            ITenantScopeFactory tenantScopes)
        {
            _scopes = scopes;
            _scope = scope;
            _clock = clock;
            _prices = prices;
            _usage = usage;
            _tenantScopes = tenantScopes;
        }

        public async Task<AiUsageOutcome> RecordAsync(AiUsageEvent usage, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(usage);
            ArgumentException.ThrowIfNullOrWhiteSpace(usage.Provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(usage.Model);

            var (siteId, tenantId, userId) = await _scope.ResolveAsync();
            if (siteId <= 0 || tenantId is null)
            {
                return new AiUsageOutcome(false, true, QuotaDecisionReason.NoSite, false, 0m, AiCostCalculator.DefaultCurrency);
            }

            var (cost, currency) = _prices.Price(usage.Provider, usage.Model, usage.InputTokens, usage.OutputTokens);
            var nowUtc = _clock.UtcNow;
            var id = QuotaIds.ForAiUsage(tenantId, siteId, usage.OperationId);

            try
            {
                using (var tenantScope = _tenantScopes.BeginScope(tenantId.Value))
                using (var scope = _scopes.CreateScope())
                {
                    var ledger = scope.ServiceProvider.GetRequiredService<IRepository<AiUsageRecordAggregate, Guid>>();
                    var existing = await ledger.GetByIdAsync(id, ct);
                    if (existing is not null)
                    {
                        // Idempotent by operation, exactly like the usage ledger.
                        return new AiUsageOutcome(true, true, QuotaDecisionReason.Within, true, existing.EstimatedCost, existing.CurrencyCode);
                    }

                    var row = AiUsageRecordAggregate.Append(
                        id, tenantId, siteId, usage.Provider, usage.Model, usage.Agent,
                        usage.InputTokens, usage.OutputTokens, usage.LatencyMs, usage.Status,
                        cost, currency, usage.QualityScore, userId, usage.OperationId, nowUtc);
                    await ledger.AddAsync(row, ct);
                    await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                throw new MeteringUnavailableException("The AI usage row could not be recorded.", ex);
            }

            // The shared metric side: one call, and the tokens it burned. A failure here is the usage recorder's
            // to report - it throws rather than dropping the fact.
            var callOutcome = await _usage.RecordAsync(new UsageEvent(CallsMetric, 1, usage.OperationId, MetricKeys.Normalize(usage.Provider)), ct);
            // A cache hit is still a CALL, and burns no tokens - the same rule the ledger row applies to itself.
            var billable = usage.Status == AiCallStatus.Cached ? 0 : usage.InputTokens + usage.OutputTokens;
            if (billable > 0)
            {
                await _usage.RecordAsync(new UsageEvent(TokensMetric, billable, usage.OperationId, MetricKeys.Normalize(usage.Provider)), ct);
            }

            return new AiUsageOutcome(true, callOutcome.WithinQuota, callOutcome.Reason, callOutcome.Idempotent, usage.Status == AiCallStatus.Cached ? 0m : cost, currency);
        }
    }
}
