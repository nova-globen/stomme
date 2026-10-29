#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Health.Persistence;
using Stomme.Modules.Health.Contributions;
using Stomme.Modules.Health.Domain;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Health.Application
{
    /// <summary>What one pass did, for the job notes.</summary>
    public sealed record HealthPassResult(int ProbesRun, int RowsWritten, int AlertsRaised, int RecoveriesSent, HealthState Worst);

    /// <summary>
    /// One tick's work for one scope: run the probes, record what they said, and decide whether anyone should be
    /// told.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separated from the job so it can be driven against a site directly, without a hosted service and without
    /// waiting for a schedule — the <c>UsageRollupPass</c> shape. Scoped: it is resolved inside the DI scope that
    /// already has the right tenant and alias.
    /// </para>
    /// <para>
    /// <b>The alert step reads and writes the same row in one method, deliberately.</b> That is the shape the
    /// widened <c>PerWriteStoreInvariantTests</c> exists to catch, and it is safe here for the reason the gate
    /// asks for: the row read is the row updated — one instance, loaded once, written once — rather than two
    /// writes through one tracked context.
    /// </para>
    /// </remarks>
    public sealed class HealthProbePass
    {
        private readonly IEnumerable<IHealthContributor> _contributors;
        private readonly IHealthRollupRepository _rollups;
        private readonly IHealthAlertStateRepository _alertStates;
        private readonly IUnitOfWork _unitOfWork;
        private readonly HealthProbeRunner _runner;
        private readonly HealthSettingsAccessor _settingsAccessor;
        private readonly ISettingRepository? _settingRepository;
        private readonly IHealthAlertSink _alerts;
        private readonly ILogger<HealthProbePass> _logger;
        private readonly TimeProvider _clock;

        public HealthProbePass(
            IEnumerable<IHealthContributor> contributors,
            IHealthRollupRepository rollups,
            IHealthAlertStateRepository alertStates,
            IUnitOfWork unitOfWork,
            HealthProbeRunner runner,
            HealthSettingsAccessor settingsAccessor,
            ISettingRepository? settingRepository,
            IHealthAlertSink alerts,
            ILogger<HealthProbePass> logger,
            TimeProvider? clock = null)
        {
            _contributors = contributors;
            _rollups = rollups;
            _alertStates = alertStates;
            _unitOfWork = unitOfWork;
            _runner = runner;
            _settingsAccessor = settingsAccessor;
            _settingRepository = settingRepository;
            _alerts = alerts;
            _logger = logger;
            _clock = clock ?? TimeProvider.System;
        }

        /// <summary>
        /// Runs every probe of <paramref name="scope"/> for <paramref name="siteId"/> and records the outcome.
        /// </summary>
        /// <param name="includeMailServer">
        /// False leaves the mail-server probe out of this pass: the job's way of spacing out its connects
        /// (<see cref="HealthSettings.MailServerProbeIntervalMinutes"/>). Nothing is recorded for it, so its last state stands.
        /// </param>
        public async Task<HealthPassResult> RunAsync(
            TenantId tenantId,
            HealthProbeScope scope,
            int siteId,
            CancellationToken cancellationToken = default,
            bool includeMailServer = true)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var context = new HealthProbeContext(siteId);

            // Loaded HERE, in an async method, and shared with the contributors through the scope's accessor -
            // the settings are read from the store and a constructor cannot await (the blocking form is banned by a build gate).
            var settings = _settingRepository is null
                ? _settingsAccessor.Current
                : await _settingsAccessor.EnsureLoadedAsync(_settingRepository).ConfigureAwait(false);

            var policy = new HealthAlertPolicy(settings.Thresholds);

            var raised = 0;
            var recovered = 0;
            var written = 0;
            var ran = 0;
            var worst = HealthState.Healthy;

            foreach (var contributor in _contributors)
            {
                var probes = await ApplicableAsync(contributor, scope, siteId, includeMailServer).ConfigureAwait(false);
                if (probes.Count == 0)
                {
                    continue;
                }

                var results = await _runner.RunAsync(probes, context, settings.ProbeTimeout, cancellationToken).ConfigureAwait(false);
                ran += results.Count;

                foreach (var result in results)
                {
                    var subject = HealthSubject.For(contributor.ContributorId, result.Name);

                    if (result.State > worst)
                    {
                        worst = result.State;
                    }

                    written += await RecordAsync(tenantId, scope, siteId, subject, result, now, cancellationToken).ConfigureAwait(false);

                    var outcome = await EvaluateAlertAsync(tenantId, scope, siteId, subject, result, policy, settings, now, cancellationToken).ConfigureAwait(false);
                    raised += outcome.Raised ? 1 : 0;
                    recovered += outcome.Recovered ? 1 : 0;
                }
            }

            return new HealthPassResult(ran, written, raised, recovered, worst);
        }

        /// <summary>The probes of this contributor that belong to this scope AND apply to this site.</summary>
        private static async Task<IReadOnlyList<IHealthProbe>> ApplicableAsync(IHealthContributor contributor, HealthProbeScope scope, int siteId, bool includeMailServer)
        {
            var inScope = contributor.Probes.Where(probe => probe.Scope == scope).ToList();
            if (inScope.Count == 0 || contributor is not StommeDependencyContributor framework)
            {
                return inScope;
            }

            // The framework contributor can say a probe does not apply to a site at all - the SMTP probe on a site
            // with no mail server. Saying nothing is the right answer there; reporting a mail server that was
            // never configured as unreachable is not.
            var applicable = new List<IHealthProbe>(inScope.Count);
            foreach (var probe in inScope)
            {
                if (!includeMailServer && probe.Name == StommeDependencyContributor.SmtpProbe)
                {
                    continue;
                }

                if (await framework.AppliesAsync(probe, siteId).ConfigureAwait(false))
                {
                    applicable.Add(probe);
                }
            }

            return applicable;
        }

        /// <summary>Upserts the live Minute row, and trims the one 48 hours behind it.</summary>
        private async Task<int> RecordAsync(
            TenantId tenantId,
            HealthProbeScope scope,
            int siteId,
            HealthSubject subject,
            HealthProbeResult result,
            DateTime now,
            CancellationToken cancellationToken)
        {
            var totals = HealthRollupAggregate.Fold(new[] { result });
            if (totals is null)
            {
                return 0;
            }

            var start = HealthRollupAggregate.Snap(HealthRollupWindow.Minute, now);
            var id = HealthIds.ForRollup(tenantId, scope, siteId, subject, HealthRollupWindow.Minute, start);
            var existing = await _rollups.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);

            if (existing is null)
            {
                var row = HealthRollupAggregate.For(tenantId, scope, siteId, subject, HealthRollupWindow.Minute, start);
                row.Accumulate(totals);
                await _rollups.AddAsync(row, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Two ticks inside one minute saw two checks. Overwriting would throw the first away.
                existing.Accumulate(totals);
                await _rollups.UpdateAsync(existing, cancellationToken).ConfigureAwait(false);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // Trim ON WRITE, by deterministic id: the Minute series stays bounded even when PurgeJob is disabled,
            // which an operator is entitled to do. The hourly sweep in the rollup job catches whatever a gap in
            // ticks left behind.
            await TrimAsync(tenantId, scope, siteId, subject, start, cancellationToken).ConfigureAwait(false);
            return 1;
        }

        /// <summary>How long the live Minute series is kept. Two days, so a weekend outage is still visible.</summary>
        public static readonly TimeSpan MinuteRetention = TimeSpan.FromHours(48);

        private async Task TrimAsync(
            TenantId tenantId,
            HealthProbeScope scope,
            int siteId,
            HealthSubject subject,
            DateTime start,
            CancellationToken cancellationToken)
        {
            var stale = HealthIds.ForRollup(tenantId, scope, siteId, subject, HealthRollupWindow.Minute, start - MinuteRetention);
            var row = await _rollups.GetByIdAsync(stale, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                return;
            }

            await _rollups.DeleteAsync(row, cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<(bool Raised, bool Recovered)> EvaluateAlertAsync(
            TenantId tenantId,
            HealthProbeScope scope,
            int siteId,
            HealthSubject subject,
            HealthProbeResult result,
            HealthAlertPolicy policy,
            HealthSettings settings,
            DateTime now,
            CancellationToken cancellationToken)
        {
            var row = await _alertStates.GetAsync(tenantId, scope, siteId, subject, cancellationToken).ConfigureAwait(false);
            var isNew = row is null;
            row ??= HealthAlertStateAggregate.For(tenantId, scope, siteId, subject, now);

            var previous = row.Observe(result.State, result.Description, now);
            var raised = false;
            var recovered = false;

            var transition = policy.OnTransition(previous, result.State);
            switch (transition.Kind)
            {
                case HealthAlertActionKind.ScheduleRecheck:
                    row.ScheduleRecheck(transition.State, transition.Delay, now);
                    break;

                case HealthAlertActionKind.DispatchRecovery:
                    // EXACTLY once per raised alert: if nothing was ever announced, there is nothing to recover
                    // from, and a recovery notice would be the first anyone heard of a problem that is over.
                    if (row.HasOutstandingAlert)
                    {
                        await _alerts.RecoveredAsync(new HealthAlertNotice(scope, siteId, subject, HealthState.Healthy, row.LastAlertedState, result, row), cancellationToken).ConfigureAwait(false);
                        row.Clear(now, settings.Thresholds.Cooldown);
                        recovered = true;
                    }
                    else
                    {
                        row.ClearRecheck();
                    }

                    break;
            }

            // A due re-check is swept here rather than by a deferred message: nothing depends on a bus being
            // enabled, and a restart cannot lose it.
            if (!recovered && row.RecheckIsDue(now) && row.ScheduledForState is { } scheduled)
            {
                var recheck = policy.OnRecheck(scheduled, row.CurrentState);
                if (recheck.Kind == HealthAlertActionKind.RaiseAlert)
                {
                    if (row.InCooldown(now))
                    {
                        // Flapping either side of the threshold must not re-alert forever; that is how a monitor
                        // teaches its operators to ignore it.
                        //
                        // But the cooldown DELAYS a raise, it does not cancel one - and getting that wrong was a
                        // real defect, caught by A_flap_inside_the_cooldown_does_not_raise_again. Simply clearing
                        // the re-check here meant a dependency that stayed broken through the cooldown was never
                        // announced at all: a sustained Unhealthy state produces no further TRANSITION, so nothing
                        // would ever arm another re-check, and the outage stayed silent until it recovered and
                        // broke again. Re-arming for the moment the cooldown ends keeps the flap quiet AND still
                        // tells someone about a problem that is genuinely not going away.
                        var until = HealthTime.AsUtc(row.CooldownUntilUtc!.Value);
                        _logger.LogInformation(
                            "Health alert for {Subject} deferred to {Until:o} by the cooldown.", subject.Value, until);
                        row.ScheduleRecheck(recheck.State, until - now, now);
                    }
                    else
                    {
                        await _alerts.RaisedAsync(new HealthAlertNotice(scope, siteId, subject, recheck.State, row.LastAlertedState, result, row), cancellationToken).ConfigureAwait(false);
                        row.Raise(recheck.State, now);
                        raised = true;
                    }
                }
                else
                {
                    // Improved below what was armed: the blip stays silent.
                    row.ClearRecheck();
                }
            }

            if (isNew)
            {
                await _alertStates.AddAsync(row, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _alertStates.UpdateAsync(row, cancellationToken).ConfigureAwait(false);
            }

            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return (raised, recovered);
        }
    }
}
