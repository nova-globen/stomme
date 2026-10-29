#nullable enable
using System;
using Stomme.Modules.Metering.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The quota's own rules, asserted on the aggregate.
    /// </summary>
    /// <remarks>
    /// Written to cover what the drives here leave unasserted: they prove the wiring on three stores, and nothing at
    /// all asserted Reset, Unlimited, a window rolling over, an override lapsing, or Suspended - every one a rule an
    /// operator relies on. Each case takes its clock as an argument, which is why none of them needs a host.
    /// </remarks>
    public class QuotaAggregateBehaviourTests
    {
        private static readonly DateTime Noon = new(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);

        private static QuotaAggregate Quota(QuotaPeriod period, long limit, int warnAtPercent = 0) =>
            QuotaAggregate.Define(Guid.NewGuid(), TenantId.Default, 1, "tests.units", period, limit, warnAtPercent, Noon);

        [Fact]
        public void Capacity_gates_AT_the_limit_not_before_it()
        {
            var quota = Quota(QuotaPeriod.Monthly, 5);
            quota.Apply(4, Noon);

            Assert.True(quota.HasCapacityFor(1, Noon));
            Assert.False(quota.HasCapacityFor(2, Noon));
        }

        [Fact]
        public void The_warning_and_the_exceeded_crossing_are_each_raised_ONCE_per_window()
        {
            var quota = Quota(QuotaPeriod.Monthly, 10, warnAtPercent: 80);

            Assert.Equal(QuotaCrossing.None, quota.Apply(7, Noon));
            Assert.Equal(QuotaCrossing.Warning, quota.Apply(1, Noon));     // 8 of 10 = the threshold
            Assert.Equal(QuotaCrossing.None, quota.Apply(1, Noon));        // still warned, not again
            Assert.Equal(QuotaCrossing.None, quota.Apply(1, Noon));        // AT the limit is within it
            Assert.Equal(QuotaCrossing.Exceeded, quota.Apply(1, Noon));    // past it
            Assert.Equal(QuotaCrossing.None, quota.Apply(5, Noon));        // and not again
            Assert.Equal(16, quota.Consumed);                              // over-quota usage is still counted
        }

        [Fact]
        public void Redefining_changes_the_allowance_and_keeps_the_counter_and_the_window()
        {
            var quota = Quota(QuotaPeriod.Monthly, 5);
            quota.Apply(4, Noon);
            var window = quota.PeriodStartUtc;

            quota.Redefine(100, 50);

            Assert.Equal(100, quota.Limit);
            Assert.Equal(50, quota.WarnAtPercent);
            Assert.Equal(4, quota.Consumed);
            Assert.Equal(window, quota.PeriodStartUtc);
            Assert.Throws<ArgumentOutOfRangeException>(() => quota.Redefine(-2, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => quota.Redefine(5, 101));
        }

        [Fact]
        public void A_reset_zeroes_the_counter_and_lets_the_crossings_be_raised_again()
        {
            var quota = Quota(QuotaPeriod.Monthly, 2, warnAtPercent: 50);
            quota.Apply(1, Noon);
            Assert.Equal(QuotaCrossing.Exceeded, quota.Apply(2, Noon));

            quota.Reset(Noon);

            Assert.Equal(0, quota.Consumed);
            Assert.False(quota.WarningRaised);
            Assert.False(quota.ExceededRaised);
            Assert.True(quota.HasCapacityFor(2, Noon));
            Assert.Equal(QuotaCrossing.Warning, quota.Apply(1, Noon));
        }

        [Fact]
        public void A_DAILY_window_rolls_over_at_the_UTC_day_and_a_MONTHLY_one_at_the_month()
        {
            var daily = Quota(QuotaPeriod.Daily, 5);
            daily.Apply(5, Noon);
            Assert.False(daily.HasCapacityFor(1, Noon));
            Assert.False(daily.HasCapacityFor(1, Noon.Date.AddDays(1).AddTicks(-1)));   // 23:59:59.9999999 - the same day
            Assert.True(daily.HasCapacityFor(1, Noon.Date.AddDays(1)));                 // 00:00 - a new one

            // Capacity is answered from the calendar; the counter itself rolls on the next fold.
            Assert.Equal(5, daily.Consumed);
            daily.Apply(1, Noon.Date.AddDays(1));
            Assert.Equal(1, daily.Consumed);
            Assert.Equal(Noon.Date.AddDays(1), daily.PeriodStartUtc);

            var monthly = Quota(QuotaPeriod.Monthly, 5);
            monthly.Apply(5, Noon);
            Assert.False(monthly.HasCapacityFor(1, new DateTime(2026, 3, 31, 23, 59, 59, DateTimeKind.Utc)));
            Assert.True(monthly.HasCapacityFor(1, new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)));
        }

        [Fact]
        public void A_TOTAL_quota_never_rolls_over()
        {
            var total = Quota(QuotaPeriod.Total, 5);
            total.Apply(5, Noon);

            Assert.False(total.HasCapacityFor(1, Noon.AddYears(10)));
            total.Apply(1, Noon.AddYears(10));
            Assert.Equal(6, total.Consumed);
        }

        [Fact]
        public void An_UNLIMITED_quota_counts_and_never_refuses_or_crosses()
        {
            var quota = Quota(QuotaPeriod.Monthly, QuotaAggregate.UnlimitedLimit, warnAtPercent: 80);

            Assert.True(quota.IsUnlimited());
            Assert.Equal(QuotaCrossing.None, quota.Apply(long.MaxValue / 4, Noon));
            Assert.True(quota.HasCapacityFor(long.MaxValue / 4, Noon));
            Assert.Equal(0, quota.ConsumedPercent());

            // ...and a limited one can be MADE unlimited, which the stored flag then says too.
            var limited = Quota(QuotaPeriod.Monthly, 1);
            limited.Redefine(QuotaAggregate.UnlimitedLimit, 0);
            Assert.True(limited.Unlimited);
            Assert.True(limited.HasCapacityFor(1_000_000, Noon));
        }

        [Fact]
        public void An_OVERRIDE_restores_access_until_it_expires_and_lets_the_exceeded_notice_fire_again()
        {
            var quota = Quota(QuotaPeriod.Monthly, 5);
            Assert.Equal(QuotaCrossing.Exceeded, quota.Apply(6, Noon));
            Assert.False(quota.HasCapacityFor(1, Noon));

            var expires = Noon.AddHours(2);
            quota.GrantOverride(10, expires);

            Assert.Equal(15, quota.EffectiveLimit(Noon));
            Assert.True(quota.HasCapacityFor(9, Noon));
            Assert.False(quota.HasCapacityFor(10, Noon));

            // AT the expiry instant the override is over - the boundary is exclusive.
            Assert.Equal(5, quota.EffectiveLimit(expires));
            Assert.False(quota.HasCapacityFor(1, expires));
            Assert.Equal(15, quota.EffectiveLimit(expires.AddTicks(-1)));

            // Granting one re-arms the exceeded notice: the operator is told again when the NEW ceiling goes.
            Assert.Equal(QuotaCrossing.Exceeded, quota.Apply(10, Noon));

            // No expiry means "for this window", and a negative grant is refused.
            quota.GrantOverride(100, null);
            Assert.Equal(105, quota.EffectiveLimit(Noon.AddYears(1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => quota.GrantOverride(-1, null));
        }

        [Fact]
        public void A_SUSPENDED_quota_still_counts_and_neither_refuses_nor_notifies()
        {
            var quota = Quota(QuotaPeriod.Monthly, 2, warnAtPercent: 50);
            quota.Suspend();

            Assert.Equal(QuotaCrossing.None, quota.Apply(10, Noon));
            Assert.True(quota.HasCapacityFor(1_000, Noon));
            Assert.Equal(10, quota.Consumed);

            // Resumed, it enforces against what was counted while it was suspended.
            quota.Resume();
            Assert.False(quota.HasCapacityFor(1, Noon));
        }

        [Fact]
        public void Nonsense_is_refused_at_definition()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => QuotaAggregate.Define(Guid.NewGuid(), TenantId.Default, 0, "m", QuotaPeriod.Total, 1, 0, Noon));
            Assert.Throws<ArgumentOutOfRangeException>(() => QuotaAggregate.Define(Guid.NewGuid(), TenantId.Default, 1, "m", QuotaPeriod.Total, -5, 0, Noon));
            Assert.Throws<ArgumentOutOfRangeException>(() => QuotaAggregate.Define(Guid.NewGuid(), TenantId.Default, 1, "m", QuotaPeriod.Total, 1, -1, Noon));
            Assert.ThrowsAny<ArgumentException>(() => QuotaAggregate.Define(Guid.NewGuid(), TenantId.Default, 1, " ", QuotaPeriod.Total, 1, 0, Noon));
            Assert.Throws<ArgumentOutOfRangeException>(() => Quota(QuotaPeriod.Total, 1).Apply(0, Noon));
        }
    }
}
