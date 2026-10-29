#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Stomme.Health;
using Stomme.Modules.Health.Application;
using Stomme.Modules.Health.Domain;
using Stomme.Tenancy.Domain;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// The pure half of the health monitor: the rollup math, the transition machine, and the two guards
    /// that stop a row being stored under the wrong identity.
    /// </summary>
    /// <remarks>
    /// These need no store and no host, which is the point — the arithmetic and the state machine are where a
    /// monitoring feature is wrong in ways that still look green, so they are tested where every case can be
    /// reached cheaply rather than only through a job that takes a minute to arrange.
    /// </remarks>
    public class HealthDomainTests
    {
        private readonly ITestOutputHelper _output;

        public HealthDomainTests(ITestOutputHelper output) => _output = output;

        private static readonly TenantId Tenant = TenantId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        private static readonly HealthSubject Subject = HealthSubject.For("stomme", "store");
        private static readonly HealthAlertThresholds Thresholds = new(
            UnhealthyAlertAfter: TimeSpan.FromMinutes(2),
            DegradedAlertAfter: TimeSpan.FromMinutes(5),
            Cooldown: TimeSpan.FromMinutes(30));

        private static HealthProbeResult Result(HealthState state, double ms) =>
            new("store", "PostgreSQL", state, null, TimeSpan.FromMilliseconds(ms));

        // ---- the lifted rollup math ------------------------------------------------------------------------

        [Fact]
        public void An_empty_window_folds_to_nothing_rather_than_to_zeroes()
        {
            // A row of zeroes and a window nothing ran in are different facts. Conflating them would turn "the
            // probe never ran" into "the probe ran and found nothing wrong".
            Assert.Null(HealthRollupAggregate.Fold(Array.Empty<HealthProbeResult>()));
            Assert.Null(HealthRollupAggregate.Downsample(Array.Empty<HealthRollupAggregate>()));
        }

        [Fact]
        public void The_fold_counts_by_state_and_rounds_latency_and_availability_to_two_places()
        {
            var totals = HealthRollupAggregate.Fold(new[]
            {
                Result(HealthState.Healthy, 10),
                Result(HealthState.Healthy, 20),
                Result(HealthState.Degraded, 30),
                Result(HealthState.Unhealthy, 41)
            })!;

            _output.WriteLine($"[totals] {totals}");

            Assert.Equal(4, totals.Checks);
            Assert.Equal(2, totals.Healthy);
            Assert.Equal(1, totals.Degraded);
            Assert.Equal(1, totals.Unhealthy);
            Assert.Equal(25.25, totals.AverageLatencyMs);   // (10+20+30+41)/4
            Assert.Equal(41d, totals.MaxLatencyMs);
            Assert.Equal(50d, totals.AvailabilityPercent);  // 2 of 4 healthy
        }

        [Fact]
        public void Availability_is_the_healthy_share_so_degraded_does_not_count_as_available()
        {
            // Worth pinning explicitly: "not unhealthy" and "healthy" are different, and treating degraded as
            // available is the kind of generosity that makes an availability figure meaningless.
            var totals = HealthRollupAggregate.Fold(new[] { Result(HealthState.Healthy, 1), Result(HealthState.Degraded, 1) })!;
            Assert.Equal(50d, totals.AvailabilityPercent);
        }

        [Fact]
        public void Downsampling_agrees_with_folding_the_same_results_directly()
        {
            // The property that keeps a Day row from contradicting the Hour rows it is made of.
            var first = new[] { Result(HealthState.Healthy, 10), Result(HealthState.Healthy, 20), Result(HealthState.Unhealthy, 60) };
            var second = new[] { Result(HealthState.Healthy, 30), Result(HealthState.Degraded, 40) };

            var direct = HealthRollupAggregate.Fold(first.Concat(second).ToList())!;
            var viaRows = HealthRollupAggregate.Downsample(new[] { Row(first), Row(second) })!;

            _output.WriteLine($"[direct]  {direct}");
            _output.WriteLine($"[via rows] {viaRows}");

            Assert.Equal(direct.Checks, viaRows.Checks);
            Assert.Equal(direct.Healthy, viaRows.Healthy);
            Assert.Equal(direct.Degraded, viaRows.Degraded);
            Assert.Equal(direct.Unhealthy, viaRows.Unhealthy);
            Assert.Equal(direct.MaxLatencyMs, viaRows.MaxLatencyMs);
            Assert.Equal(direct.AvailabilityPercent, viaRows.AvailabilityPercent);
            Assert.Equal(direct.AverageLatencyMs, viaRows.AverageLatencyMs);

            static HealthRollupAggregate Row(IReadOnlyCollection<HealthProbeResult> results)
            {
                var row = HealthRollupAggregate.For(Tenant, HealthProbeScope.Installation, 0, Subject, HealthRollupWindow.Minute, new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc));
                var totals = HealthRollupAggregate.Fold(results)!;
                row.Set(totals.Checks, totals.Healthy, totals.Degraded, totals.Unhealthy, totals.AverageLatencyMs, totals.MaxLatencyMs, totals.AvailabilityPercent);
                return row;
            }
        }

        [Fact]
        public void Downsampling_weights_the_average_by_check_count_not_by_row()
        {
            // Two rows of very different sizes: ten checks at 10 ms, one at 100 ms. Averaging the AVERAGES would
            // give 55 - letting a single quiet minute drag the figure up by a factor of three. Weighting by check
            // count gives 200/11 = 18.18, which is what the eleven checks actually averaged.
            var busy = HealthRollupAggregate.For(Tenant, HealthProbeScope.Installation, 0, Subject, HealthRollupWindow.Minute, new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc));
            busy.Set(checks: 10, healthy: 10, degraded: 0, unhealthy: 0, averageLatencyMs: 10, maxLatencyMs: 10, availabilityPercent: 100);

            var quiet = HealthRollupAggregate.For(Tenant, HealthProbeScope.Installation, 0, Subject, HealthRollupWindow.Minute, new DateTime(2026, 9, 20, 8, 1, 0, DateTimeKind.Utc));
            quiet.Set(checks: 1, healthy: 1, degraded: 0, unhealthy: 0, averageLatencyMs: 100, maxLatencyMs: 100, availabilityPercent: 100);

            var totals = HealthRollupAggregate.Downsample(new[] { busy, quiet })!;
            _output.WriteLine($"[weighted average] {totals.AverageLatencyMs} (averaging the averages would be 55)");

            Assert.Equal(11, totals.Checks);
            Assert.Equal(18.18, totals.AverageLatencyMs);   // (10*10 + 100*1) / 11
            Assert.NotEqual(55d, totals.AverageLatencyMs);
        }

        // ---- identity and scope guards ---------------------------------------------------------------------

        [Fact]
        public void The_window_start_is_snapped_in_utc_and_an_unspecified_instant_is_not_shifted()
        {
            // A defect guarded here before it can happen again: a stored instant comes back Unspecified
            // and is ALREADY UTC. Converting it subtracts the machine offset - invisible on a UTC machine such as
            // CI, which is why this asserts ticks rather than behaviour.
            var stored = new DateTime(2026, 9, 20, 0, 30, 0, DateTimeKind.Unspecified);

            Assert.Equal(new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc).Ticks, HealthRollupAggregate.Snap(HealthRollupWindow.Hour, stored).Ticks);
            Assert.Equal(new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc).Ticks, HealthRollupAggregate.Snap(HealthRollupWindow.Day, stored).Ticks);
            Assert.Equal(DateTimeKind.Utc, HealthRollupAggregate.Snap(HealthRollupWindow.Minute, stored).Kind);
        }

        [Fact]
        public void A_site_scope_row_needs_a_real_site_and_a_wider_scope_must_not_claim_one()
        {
            // SiteId = -1 is what the mock alias carries, so it must never be usable as "installation-wide".
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                HealthRollupAggregate.For(Tenant, HealthProbeScope.Site, 0, Subject, HealthRollupWindow.Minute, DateTime.UtcNow));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                HealthRollupAggregate.For(Tenant, HealthProbeScope.Site, -1, Subject, HealthRollupWindow.Minute, DateTime.UtcNow));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                HealthAlertStateAggregate.For(Tenant, HealthProbeScope.Installation, 7, Subject, DateTime.UtcNow));

            // And the legitimate shapes are accepted.
            HealthRollupAggregate.For(Tenant, HealthProbeScope.Site, 7, Subject, HealthRollupWindow.Minute, DateTime.UtcNow);
            HealthAlertStateAggregate.For(Tenant, HealthProbeScope.Installation, 0, Subject, DateTime.UtcNow);
        }

        [Fact]
        public void The_subject_is_normalised_once_so_two_spellings_are_one_row()
        {
            // If they were two rows, the alert machine would see a dependency flapping between two identities and
            // re-raise forever.
            var a = HealthSubject.For("Stomme", "Store");
            var b = HealthSubject.Create(" stomme : store ".Replace(" : ", ":"));

            _output.WriteLine($"[subjects] '{a.Value}' vs '{b.Value}'");
            Assert.Equal(a, b);
            Assert.Equal(
                HealthIds.ForRollup(Tenant, HealthProbeScope.Site, 7, a, HealthRollupWindow.Hour, new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc)),
                HealthIds.ForRollup(Tenant, HealthProbeScope.Site, 7, b, HealthRollupWindow.Hour, new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc)));
        }

        [Fact]
        public void The_rollup_id_separates_sites_scopes_windows_and_periods()
        {
            var at = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
            var ids = new[]
            {
                HealthIds.ForRollup(Tenant, HealthProbeScope.Site, 7, Subject, HealthRollupWindow.Hour, at),
                HealthIds.ForRollup(Tenant, HealthProbeScope.Site, 8, Subject, HealthRollupWindow.Hour, at),
                HealthIds.ForRollup(Tenant, HealthProbeScope.Tenant, 0, Subject, HealthRollupWindow.Hour, at),
                HealthIds.ForRollup(Tenant, HealthProbeScope.Site, 7, Subject, HealthRollupWindow.Day, at),
                HealthIds.ForRollup(Tenant, HealthProbeScope.Site, 7, Subject, HealthRollupWindow.Hour, at.AddHours(1)),
                HealthIds.ForRollup(Tenant, HealthProbeScope.Site, 7, HealthSubject.For("stomme", "cache"), HealthRollupWindow.Hour, at)
            };

            // Leaving the site out of the key would let one site's probe overwrite another's - and make one
            // site's health readable by fetching the other's id.
            Assert.Equal(ids.Length, ids.Distinct().Count());
        }

        [Fact]
        public void Two_tenants_sharing_one_database_and_one_site_do_not_collide()
        {
            // Site-per-alias is a supported topology and MeteringJobTenantScopeTests builds it: two tenants, ONE
            // physical database, the SAME site id. Without the tenant in the key, both tenants' rows for that site
            // would carry the same primary key - a straight collision in one table, after which the tenant query
            // filter hides the survivor from at least one of them. This is the guard for that.
            var other = TenantId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));
            var at = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

            Assert.NotEqual(
                HealthIds.ForRollup(Tenant, HealthProbeScope.Site, 1, Subject, HealthRollupWindow.Hour, at),
                HealthIds.ForRollup(other, HealthProbeScope.Site, 1, Subject, HealthRollupWindow.Hour, at));

            Assert.NotEqual(
                HealthIds.ForAlertState(Tenant, HealthProbeScope.Site, 1, Subject),
                HealthIds.ForAlertState(other, HealthProbeScope.Site, 1, Subject));

            _output.WriteLine("[tenant A] " + HealthIds.ForAlertState(Tenant, HealthProbeScope.Site, 1, Subject));
            _output.WriteLine("[tenant B] " + HealthIds.ForAlertState(other, HealthProbeScope.Site, 1, Subject));
        }

        [Fact]
        public void An_alert_row_is_one_per_scope_and_subject_not_one_per_tick()
        {
            // This is what makes "one shared store outage raises ONE alert" true.
            var at = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
            Assert.Equal(
                HealthIds.ForAlertState(Tenant, HealthProbeScope.Installation, 0, Subject),
                HealthIds.ForAlertState(Tenant, HealthProbeScope.Installation, 0, Subject));
            Assert.NotEqual(
                HealthIds.ForAlertState(Tenant, HealthProbeScope.Site, 7, Subject),
                HealthIds.ForAlertState(Tenant, HealthProbeScope.Site, 8, Subject));
            _output.WriteLine($"[alert id stable across time] {HealthIds.ForAlertState(Tenant, HealthProbeScope.Installation, 0, Subject)} at {at:O}");
        }

        // ---- the transition machine ------------------------------------------------------------------------

        [Fact]
        public void Recovery_is_decided_before_anything_else_so_it_can_actually_fire()
        {
            // The refuted design compared states with "worse than last alerted", under which Healthy(0) is never
            // greater than Unhealthy(2) and a recovery could NEVER be sent. Both recovery paths are asserted.
            var policy = new HealthAlertPolicy(Thresholds);

            Assert.Equal(HealthAlertActionKind.DispatchRecovery, policy.OnTransition(HealthState.Unhealthy, HealthState.Healthy).Kind);
            Assert.Equal(HealthAlertActionKind.DispatchRecovery, policy.OnTransition(HealthState.Degraded, HealthState.Healthy).Kind);
        }

        [Fact]
        public void Going_bad_arms_a_recheck_rather_than_alerting_immediately()
        {
            var policy = new HealthAlertPolicy(Thresholds);

            var toUnhealthy = policy.OnTransition(HealthState.Healthy, HealthState.Unhealthy);
            Assert.Equal(HealthAlertActionKind.ScheduleRecheck, toUnhealthy.Kind);
            Assert.Equal(TimeSpan.FromMinutes(2), toUnhealthy.Delay);

            var toDegraded = policy.OnTransition(HealthState.Healthy, HealthState.Degraded);
            Assert.Equal(HealthAlertActionKind.ScheduleRecheck, toDegraded.Kind);
            Assert.Equal(TimeSpan.FromMinutes(5), toDegraded.Delay);

            // Escalation degraded -> unhealthy arms the SHORTER unhealthy threshold.
            var escalation = policy.OnTransition(HealthState.Degraded, HealthState.Unhealthy);
            Assert.Equal(HealthAlertActionKind.ScheduleRecheck, escalation.Kind);
            Assert.Equal(TimeSpan.FromMinutes(2), escalation.Delay);
        }

        [Fact]
        public void An_improvement_and_an_unchanged_state_arm_nothing()
        {
            var policy = new HealthAlertPolicy(Thresholds);

            // Unhealthy -> Degraded is getting BETTER. Arming here would alert twice for one outage.
            Assert.Equal(HealthAlertActionKind.None, policy.OnTransition(HealthState.Unhealthy, HealthState.Degraded).Kind);

            // A second bad tick at the same state is not a transition.
            Assert.Equal(HealthAlertActionKind.None, policy.OnTransition(HealthState.Unhealthy, HealthState.Unhealthy).Kind);
            Assert.Equal(HealthAlertActionKind.None, policy.OnTransition(HealthState.Healthy, HealthState.Healthy).Kind);
        }

        [Fact]
        public void A_recheck_raises_only_while_the_problem_is_still_at_least_as_bad()
        {
            var policy = new HealthAlertPolicy(Thresholds);

            Assert.Equal(HealthAlertActionKind.RaiseAlert, policy.OnRecheck(HealthState.Unhealthy, HealthState.Unhealthy).Kind);
            Assert.Equal(HealthAlertActionKind.RaiseAlert, policy.OnRecheck(HealthState.Degraded, HealthState.Degraded).Kind);

            // Escalated past what was armed: still raises, at the worse state.
            var escalated = policy.OnRecheck(HealthState.Degraded, HealthState.Unhealthy);
            Assert.Equal(HealthAlertActionKind.RaiseAlert, escalated.Kind);
            Assert.Equal(HealthState.Unhealthy, escalated.State);

            // Recovered or improved below the armed state: the blip stays silent.
            Assert.Equal(HealthAlertActionKind.None, policy.OnRecheck(HealthState.Unhealthy, HealthState.Healthy).Kind);
            Assert.Equal(HealthAlertActionKind.None, policy.OnRecheck(HealthState.Unhealthy, HealthState.Degraded).Kind);
            Assert.Equal(HealthAlertActionKind.None, policy.OnRecheck(HealthState.Degraded, HealthState.Healthy).Kind);
        }

        // ---- the durable row's own behaviour ----------------------------------------------------------------

        [Fact]
        public void The_row_remembers_a_raise_across_processes_and_clears_exactly_once()
        {
            var at = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
            var row = HealthAlertStateAggregate.For(Tenant, HealthProbeScope.Installation, 0, Subject, at);

            Assert.False(row.HasOutstandingAlert);

            var previous = row.Observe(HealthState.Unhealthy, "connection refused", at);
            Assert.Equal(HealthState.Healthy, previous);
            Assert.Equal(at, row.BadSinceUtc);

            row.ScheduleRecheck(HealthState.Unhealthy, TimeSpan.FromMinutes(2), at);
            Assert.False(row.RecheckIsDue(at.AddMinutes(1)));
            Assert.True(row.RecheckIsDue(at.AddMinutes(2)));

            row.Raise(HealthState.Unhealthy, at.AddMinutes(2));
            Assert.True(row.HasOutstandingAlert);
            Assert.Equal(1, row.AlertCount);
            Assert.Null(row.NextEvaluationOnUtc);   // raising disarms the re-check

            // A SECOND host over the same database sees the outstanding alert and does not re-raise: that is the
            // caller's check, and this is the state it reads.
            row.Observe(HealthState.Unhealthy, "connection refused", at.AddMinutes(3));
            Assert.True(row.HasOutstandingAlert);
            Assert.Equal(1, row.AlertCount);

            // Recovery clears it once; a second recovery has nothing left to clear.
            row.Observe(HealthState.Healthy, null, at.AddMinutes(10));
            row.Clear(at.AddMinutes(10), Thresholds.Cooldown);
            Assert.False(row.HasOutstandingAlert);
            Assert.Null(row.BadSinceUtc);
            Assert.True(row.InCooldown(at.AddMinutes(20)));
            Assert.False(row.InCooldown(at.AddMinutes(41)));
        }

        [Fact]
        public void Acknowledging_one_alert_does_not_acknowledge_the_next_one()
        {
            var at = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
            var row = HealthAlertStateAggregate.For(Tenant, HealthProbeScope.Site, 7, Subject, at);

            row.Observe(HealthState.Unhealthy, "down", at);
            row.Raise(HealthState.Unhealthy, at);
            row.Acknowledge(at.AddMinutes(1));
            Assert.NotNull(row.AcknowledgedOnUtc);

            row.Observe(HealthState.Healthy, null, at.AddMinutes(5));
            row.Clear(at.AddMinutes(5), Thresholds.Cooldown);

            row.Observe(HealthState.Unhealthy, "down again", at.AddHours(2));
            row.Raise(HealthState.Unhealthy, at.AddHours(2));

            // A new problem is not silently pre-acknowledged by somebody having seen the last one.
            Assert.Null(row.AcknowledgedOnUtc);
            Assert.Equal(2, row.AlertCount);
        }
    }
}
