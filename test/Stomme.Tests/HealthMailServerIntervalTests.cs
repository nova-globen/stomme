#nullable enable
using System;
using System.Collections.Generic;
using Stomme.Modules.Health.Application;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The probe job's mail-server connects run on their own interval (<c>Health:MailServerProbeIntervalMinutes</c>), so a
    /// relay that counts connections is not spent by monitoring. The job itself is driven in <c>OverrideFileSwitchTests</c>.
    /// </summary>
    public class HealthMailServerIntervalTests
    {
        private static readonly Guid Tenant = Guid.NewGuid();
        private static readonly DateTime Start = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

        [Theory]
        [InlineData(null, 1)]
        [InlineData("1", 1)]
        [InlineData("15", 15)]
        [InlineData("0", 0)]
        [InlineData("-5", 1)]
        [InlineData("soon", 1)]
        public void The_interval_is_read_defensively(string? value, int expected)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [HealthSettings.MailServerProbeIntervalKey] = value })
                .Build();

            Assert.Equal(expected, HealthSettings.MailServerProbeIntervalMinutes(configuration));
        }

        [Fact]
        public void Every_tick_probes_at_an_interval_of_one()
        {
            var schedule = new HealthMailServerSchedule();

            Assert.True(schedule.TakeIfDue(Tenant, 1, Start, 1));
            Assert.True(schedule.TakeIfDue(Tenant, 1, Start, 1));
        }

        [Fact]
        public void No_tick_probes_at_an_interval_of_zero()
        {
            Assert.False(new HealthMailServerSchedule().TakeIfDue(Tenant, 1, Start, 0));
        }

        [Fact]
        public void A_longer_interval_probes_once_per_interval_per_site()
        {
            var schedule = new HealthMailServerSchedule();

            Assert.True(schedule.TakeIfDue(Tenant, 1, Start, 15));
            Assert.True(schedule.TakeIfDue(Tenant, 2, Start, 15));
            Assert.True(schedule.TakeIfDue(Guid.NewGuid(), 1, Start, 15));

            Assert.False(schedule.TakeIfDue(Tenant, 1, Start.AddMinutes(1), 15));
            Assert.False(schedule.TakeIfDue(Tenant, 1, Start.AddMinutes(14), 15));

            // A tick a few seconds early still counts: ticks drift, and a probe skipped for that waits a whole interval more.
            Assert.True(schedule.TakeIfDue(Tenant, 1, Start.AddMinutes(15).AddSeconds(-10), 15));
            Assert.False(schedule.TakeIfDue(Tenant, 1, Start.AddMinutes(16), 15));
        }
    }
}
