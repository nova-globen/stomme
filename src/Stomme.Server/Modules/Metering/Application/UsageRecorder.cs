#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Metering;
using Stomme.Persistence.Abstractions;
using Stomme.Modules.Metering.Domain;
using Stomme.Security.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Modules.Metering.Application
{
    /// <summary>
    /// Appends usage to the ledger and folds it into the quota counter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two commits, admitted.</b> Stomme's unit of work persists nothing of its own - every repository call
    /// commits eagerly - so the ledger append and the counter update cannot share a transaction, and
    /// a single-SaveChanges atomicity is not available. Rather than pretend otherwise, the pair
    /// is made RECOVERABLE: the ledger row is written Pending, the fold runs under a lock, and the row is flipped
    /// to Applied afterwards. A crash in between leaves a Pending row that the recovery job folds.
    /// </para>
    /// <para>
    /// <b>The retry opens a fresh scope.</b> On EF the repository holds one tracking DbContext per DI scope, so a
    /// re-read after a concurrency conflict returns the SAME stale instance and every attempt would conflict
    /// identically - the identity-map trap this repository has been bitten by twice. Each attempt therefore runs
    /// in a new DI scope. RavenDB opens a session per call, so it is unaffected; that asymmetry is the point.
    /// </para>
    /// </remarks>
    public sealed class UsageRecorder : IUsageRecorder
    {
        internal const int MaxFoldAttempts = 3;

        private readonly IServiceScopeFactory _scopes;
        private readonly MeteringScope _scope;
        private readonly IMeteringClock _clock;
        private readonly ITenantScopeFactory _tenantScopes;
        private readonly QuotaCrossingNotifier _crossings;

        public UsageRecorder(IServiceScopeFactory scopes, MeteringScope scope, IMeteringClock clock, ITenantScopeFactory tenantScopes, QuotaCrossingNotifier crossings)
        {
            _scopes = scopes;
            _scope = scope;
            _clock = clock;
            _tenantScopes = tenantScopes;
            _crossings = crossings;
        }

        public async Task<UsageOutcome> RecordAsync(UsageEvent usage, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(usage);
            var metric = MetricKeys.RequireMetric(usage.Metric);
            var source = MetricKeys.RequireSource(usage.Source);
            if (usage.Quantity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(usage), "Consumption is positive.");
            }

            // Derived here, never accepted from the caller: the site from the request or the resolved alias, the
            // user from the request principal (null outside one), the time from the clock.
            var (siteId, tenantId, userId) = await _scope.ResolveAsync();
            if (siteId <= 0 || tenantId is null)
            {
                return new UsageOutcome(false, true, QuotaDecisionReason.NoSite, false);
            }
            var nowUtc = _clock.UtcNow;

            try
            {
                var ledgerId = QuotaIds.ForUsage(tenantId, siteId, metric, usage.OperationId);

                // ---- append (idempotent by operation) -------------------------------------------------------
                using (var tenantScope = _tenantScopes.BeginScope(tenantId.Value))
                using (var scope = _scopes.CreateScope())
                {
                    var ledger = scope.ServiceProvider.GetRequiredService<IRepository<UsageRecordAggregate, Guid>>();
                    var existing = await ledger.GetByIdAsync(ledgerId, ct);
                    if (existing is not null && existing.FoldState == FoldState.Applied)
                    {
                        return new UsageOutcome(true, true, QuotaDecisionReason.Within, true);
                    }

                    if (existing is null)
                    {
                        var appended = UsageRecordAggregate.Append(ledgerId, tenantId, siteId, metric, usage.Quantity, userId, source, usage.OperationId, nowUtc);
                        await ledger.AddAsync(appended, ct);
                        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(ct);
                    }
                }

                // ---- fold (serialised, retried in a FRESH scope) --------------------------------------------
                var fold = await FoldAsync(siteId, tenantId, metric, usage.Quantity, ledgerId, nowUtc, ct);
                return new UsageOutcome(true, fold.WithinQuota, fold.Reason, false);
            }
            catch (MeteringUnavailableException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new MeteringUnavailableException($"Usage for '{metric}' could not be recorded.", ex);
            }
        }

        /// <summary>
        /// Folds one operation's consumption into its counter. Used by <see cref="RecordAsync"/> and, with the
        /// row's OWN quantity and instant, by the recovery job - so a recovered fold runs through the same lock,
        /// the same retry and the same crossing notification as a live one.
        /// </summary>
        internal async Task<FoldResult> FoldAsync(
            int siteId, Tenancy.Domain.TenantId tenantId, string metric, long quantity, Guid ledgerId, DateTime nowUtc, CancellationToken ct)
        {
            var fold = await FoldUnderGateAsync(siteId, tenantId, metric, quantity, ledgerId, nowUtc, ct);

            // AFTER the fold has committed and the gate is released, never inside it: see QuotaCrossingNotifier's
            // remarks for why this is not a domain-event handler. A failure to notify never fails the recording.
            if (fold.Crossing != QuotaCrossing.None)
            {
                await _crossings.NotifyAsync(fold.Crossing, siteId, metric, fold.Consumed, fold.Limit, fold.Period, ct);
            }

            return fold;
        }

        private async Task<FoldResult> FoldUnderGateAsync(
            int siteId, Tenancy.Domain.TenantId tenantId, string metric, long quantity, Guid ledgerId, DateTime nowUtc, CancellationToken ct)
        {
            using var gateTenantScope = _tenantScopes.BeginScope(tenantId.Value);
            using var gateScope = _scopes.CreateScope();
            var gate = gateScope.ServiceProvider.GetRequiredService<QuotaFold>();

            return await gate.HoldAsync(siteId, metric, async () =>
            {
                for (var attempt = 1; attempt <= MaxFoldAttempts; attempt++)
                {
                    // A FRESH scope per attempt: EF's identity map would otherwise hand the retry the same stale
                    // aggregate and every attempt would conflict identically. The tenant is carried explicitly.
                    using var attemptTenantScope = _tenantScopes.BeginScope(tenantId.Value);
                    using var scope = _scopes.CreateScope();
                    var quotas = scope.ServiceProvider.GetRequiredService<IRepository<QuotaAggregate, Guid>>();
                    var ledger = scope.ServiceProvider.GetRequiredService<IRepository<UsageRecordAggregate, Guid>>();
                    var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                    try
                    {
                        var quota = await FindQuotaAsync(quotas, tenantId, siteId, metric, ct);
                        var reason = QuotaDecisionReason.Undefined;
                        var within = true;
                        var crossing = QuotaCrossing.None;
                        var consumed = 0L;
                        var limit = 0L;
                        var period = QuotaPeriod.Total.ToString();

                        if (quota is not null)
                        {
                            crossing = quota.Apply(quantity, nowUtc);
                            within = quota.IsUnlimited() || quota.Consumed <= quota.EffectiveLimit(nowUtc);
                            reason = quota.IsUnlimited()
                                ? QuotaDecisionReason.Unlimited
                                : within ? QuotaDecisionReason.Within : QuotaDecisionReason.Exceeded;
                            consumed = quota.Consumed;
                            limit = quota.EffectiveLimit(nowUtc);
                            period = quota.Period.ToString();
                            await quotas.UpdateAsync(quota, ct);
                        }

                        var row = await ledger.GetByIdAsync(ledgerId, ct);
                        if (row is not null && row.FoldState == FoldState.Pending)
                        {
                            row.MarkApplied();
                            await ledger.UpdateAsync(row, ct);
                        }

                        await unitOfWork.SaveChangesAsync(ct);
                        return new FoldResult(within, reason, crossing, consumed, limit, period);
                    }
                    catch (ConcurrencyConflictException)
                    {
                        // Another recorder folded first; re-read in the NEXT scope and re-apply. The LAST attempt's
                        // conflict is caught too and falls to the exhausted path below (a filter used to let it
                        // escape, failing a recording whose ledger row was already durable - what that path forbids).
                    }
                }

                // Exhausted: the ledger row stays Pending and the recovery job owns it. Throwing here would fail
                // an operation whose consumption is already durably recorded.
                return new FoldResult(true, QuotaDecisionReason.Undefined, QuotaCrossing.None, 0, 0, QuotaPeriod.Total.ToString());
            }, ct);
        }

        private static async Task<QuotaAggregate?> FindQuotaAsync(IRepository<QuotaAggregate, Guid> quotas, Tenancy.Domain.TenantId tenantId, int siteId, string metric, CancellationToken ct)
        {
            foreach (var period in new[] { QuotaPeriod.Monthly, QuotaPeriod.Daily, QuotaPeriod.Total })
            {
                var quota = await quotas.GetByIdAsync(QuotaIds.For(tenantId, siteId, metric, period), ct);
                if (quota is not null && quota.SiteId == siteId)
                {
                    return quota;
                }
            }

            return null;
        }
    }

    /// <summary>What one fold decided, and what it crossed while deciding it.</summary>
    /// <param name="WithinQuota">Whether the post-fold counter is still inside the allowance.</param>
    /// <param name="Reason">Why - including <c>Undefined</c> when no quota governs the metric.</param>
    /// <param name="Crossing">The threshold this fold crossed for the FIRST time this window, if any.</param>
    /// <param name="Consumed">The post-fold counter, for the notification's text.</param>
    /// <param name="Limit">The limit in force at the fold, including a live override.</param>
    /// <param name="Period">The window the counter belongs to, as a string - the wire never carries the enum.</param>
    public sealed record FoldResult(bool WithinQuota, QuotaDecisionReason Reason, QuotaCrossing Crossing, long Consumed, long Limit, string Period);

    /// <summary>The clock the module reads, so a test can drive a window boundary without waiting for midnight.</summary>
    public interface IMeteringClock
    {
        DateTime UtcNow { get; }
    }

    /// <summary>The production clock.</summary>
    public sealed class SystemMeteringClock : IMeteringClock
    {
        public DateTime UtcNow => DateTime.UtcNow;
    }
}
