#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Metering;
using Stomme.Persistence.Abstractions;
using Stomme.Modules.Metering.Domain;
using Stomme.Security.Abstractions;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Metering.Application
{
    /// <summary>
    /// Answers "may this happen?" before it happens - the pre-consumption entry point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The subject is always the CALLER: no user id crosses this port, so a producer cannot ask about (or charge)
    /// somebody else's allowance. A guard that took an optional user id and counted only that user's rows would, for
    /// an unknown id, return "consumed 0, allowed" - a check that answered yes to anyone who asked the
    /// right way.
    /// </para>
    /// <para>
    /// <b>Enforcement fails CLOSED.</b> When the store cannot be reached the answer is <c>Allowed=false</c> with
    /// <see cref="QuotaDecisionReason.Unavailable"/>, because an enforcement point that opens on failure is not an
    /// enforcement point. Recording is the opposite - it throws rather than silently dropping the fact - and those
    /// two postures are deliberate, not an oversight.
    /// </para>
    /// </remarks>
    public sealed class QuotaInspector : IQuotaInspector
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly MeteringScope _scope;
        private readonly QuotaLimitResolver _limits;
        private readonly IMeteringClock _clock;
        private readonly ITenantScopeFactory _tenantScopes;

        public QuotaInspector(IServiceScopeFactory scopes, MeteringScope scope, QuotaLimitResolver limits, IMeteringClock clock, ITenantScopeFactory tenantScopes)
        {
            _scopes = scopes;
            _scope = scope;
            _limits = limits;
            _clock = clock;
            _tenantScopes = tenantScopes;
        }

        public async Task<QuotaDecision> EvaluateAsync(string metric, long quantity, CancellationToken ct = default)
        {
            var key = MetricKeys.RequireMetric(metric);
            if (quantity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(quantity), "Ask about a positive quantity.");
            }

            var (siteId, tenantId, _) = await _scope.ResolveAsync();
            if (siteId <= 0 || tenantId is null)
            {
                return new QuotaDecision(false, QuotaDecisionReason.NoSite, 0, 0, 0);
            }

            try
            {
                using var tenantScope = _tenantScopes.BeginScope(tenantId.Value);
                using var scope = _scopes.CreateScope();
                var quotas = scope.ServiceProvider.GetRequiredService<IRepository<QuotaAggregate, Guid>>();
                var nowUtc = _clock.UtcNow;

                foreach (var period in new[] { QuotaPeriod.Monthly, QuotaPeriod.Daily, QuotaPeriod.Total })
                {
                    var quota = await quotas.GetByIdAsync(QuotaIds.For(tenantId, siteId, key, period), ct);
                    if (quota is null || quota.SiteId != siteId)
                    {
                        continue;
                    }

                    if (quota.IsUnlimited())
                    {
                        return new QuotaDecision(true, QuotaDecisionReason.Unlimited, QuotaAggregate.UnlimitedLimit, quota.Consumed, long.MaxValue);
                    }

                    var limit = quota.EffectiveLimit(nowUtc);
                    var window = QuotaAggregate.WindowStart(quota.Period, nowUtc);
                    var consumed = window > quota.PeriodStartUtc ? 0 : quota.Consumed;
                    var allowed = quota.HasCapacityFor(quantity, nowUtc);
                    return new QuotaDecision(
                        allowed,
                        allowed ? QuotaDecisionReason.Within : QuotaDecisionReason.Exceeded,
                        limit,
                        consumed,
                        Math.Max(0, limit - consumed));
                }

                // No quota row: permissive, and the usage is still recorded. An undefined metric is not a denial -
                // that would turn every new metric into an outage on the day it is first emitted.
                var configured = await _limits.ResolveAsync(key, siteId, ct);
                return configured is null
                    ? new QuotaDecision(true, QuotaDecisionReason.Undefined, 0, 0, long.MaxValue)
                    : new QuotaDecision(true, configured.Unlimited ? QuotaDecisionReason.Unlimited : QuotaDecisionReason.Within, configured.Unlimited ? QuotaAggregate.UnlimitedLimit : configured.Units, 0, configured.Unlimited ? long.MaxValue : configured.Units);
            }
            catch (Exception)
            {
                // Fails CLOSED - see the type remarks.
                return new QuotaDecision(false, QuotaDecisionReason.Unavailable, 0, 0, 0);
            }
        }
    }
}
