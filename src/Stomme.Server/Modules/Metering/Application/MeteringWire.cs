#nullable enable
using System;
using Stomme.Models.Metering;
using Stomme.Modules.Metering.Domain;
using Stomme.Shared;

namespace Stomme.Modules.Metering.Application
{
    /// <summary>
    /// Aggregate to view model, and the one place the handled site id is resolved.
    /// </summary>
    /// <remarks>
    /// <b>The site is RESOLVED, never accepted.</b> A command that took the tenant from the request
    /// body would let a caller grant themselves capacity on another tenant. Here no metering request carries a site
    /// or a tenant at all: <see cref="RequireSiteId"/> reads the alias the request was routed with, and a scope
    /// that resolved no site throws rather than defaulting to anything.
    /// </remarks>
    public static class MeteringWire
    {
        public static int RequireSiteId(SiteState? siteState)
        {
            var siteId = siteState?.Alias?.SiteId ?? -1;
            if (siteId <= 0)
            {
                throw new InvalidOperationException("No site is resolved in this scope.");
            }

            return siteId;
        }

        public static QuotaView ToView(QuotaAggregate quota, DateTime nowUtc)
        {
            var effective = quota.EffectiveLimit(nowUtc);
            return new QuotaView
            {
                QuotaId = quota.Id,
                Metric = quota.Metric,
                Period = quota.Period.ToString(),
                Limit = quota.Limit,
                Unlimited = quota.IsUnlimited(),
                WarnAtPercent = quota.WarnAtPercent,
                Consumed = quota.Consumed,
                Remaining = quota.IsUnlimited() ? -1 : Math.Max(0, effective - quota.Consumed),
                ConsumedPercent = quota.ConsumedPercent(),
                PeriodStartUtc = quota.PeriodStartUtc,
                Status = quota.Status.ToString(),
                OverrideAdditional = quota.OverrideAdditional,
                OverrideExpiresUtc = quota.OverrideExpiresUtc
            };
        }

        public static UsageRecordView ToView(UsageRecordAggregate row) => new UsageRecordView
        {
            UsageId = row.Id,
            Metric = row.Metric,
            Quantity = row.Quantity,
            UserId = row.UserId,
            Source = row.Source ?? string.Empty,
            OccurredOnUtc = row.OccurredOnUtc,
            FoldState = row.FoldState.ToString()
        };

        public static AiUsageRecordView ToView(AiUsageRecordAggregate row) => new AiUsageRecordView
        {
            AiUsageId = row.Id,
            Provider = row.Provider,
            Model = row.Model,
            Agent = row.Agent,
            InputTokens = row.InputTokens,
            OutputTokens = row.OutputTokens,
            LatencyMs = row.LatencyMs,
            Status = row.Status.ToString(),
            EstimatedCost = row.EstimatedCost,
            CurrencyCode = row.CurrencyCode,
            QualityScore = row.QualityScore,
            UserId = row.UserId,
            OccurredOnUtc = row.OccurredOnUtc
        };

        public static AiUsageRollupView ToView(AiUsageRollupAggregate row) => new AiUsageRollupView
        {
            DayUtc = row.DayUtc,
            Provider = row.ProviderNormalized,
            Model = row.ModelNormalized,
            Agent = row.AgentNormalized,
            Calls = row.Calls,
            InputTokens = row.InputTokens,
            OutputTokens = row.OutputTokens,
            BillableTokens = row.BillableTokens(),
            Errors = row.Errors,
            EstimatedCost = row.EstimatedCost,
            CurrencyCode = row.CurrencyCode
        };

        /// <summary>Parses a wire period, refusing anything that is not one of the three windows.</summary>
        public static QuotaPeriod ParsePeriod(string? period) =>
            Enum.TryParse<QuotaPeriod>(period, ignoreCase: true, out var parsed)
                ? parsed
                : throw new ArgumentException($"'{period}' is not a quota period (Total, Daily or Monthly).", nameof(period));
    }
}
