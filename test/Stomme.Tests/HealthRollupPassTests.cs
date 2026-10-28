#nullable enable
using System;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Modules.Health.Application;
using Stomme.Modules.Health.Domain;
using Stomme.Tenancy.Domain;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// Folding the live Minute series up into Hours and Days, and sweeping what the trim-on-write missed.
    /// </summary>
    public class HealthRollupPassTests
    {
        private readonly ITestOutputHelper _output;

        public HealthRollupPassTests(ITestOutputHelper output) => _output = output;

        private static readonly TenantId Tenant = TenantId.From(Guid.Parse("55555555-5555-5555-5555-555555555555"));
        private static readonly HealthSubject Subject = HealthSubject.For("stomme", "store");
        private static readonly DateTime Noon = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

        private sealed class Clock(DateTime now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
        }

        [Fact]
        public async Task Minutes_fold_into_an_hour_and_the_hour_agrees_with_the_minutes()
        {
            var rollups = new InMemoryHealthRollups();
            var pass = new HealthRollupPass(rollups, new HealthNoOpUnitOfWork(), new Clock(Noon.AddHours(1)));

            // Three minutes inside one hour: two healthy, one unhealthy.
            Minute(rollups, Noon, healthy: 1, unhealthy: 0, latency: 10);
            Minute(rollups, Noon.AddMinutes(1), healthy: 1, unhealthy: 0, latency: 20);
            Minute(rollups, Noon.AddMinutes(2), healthy: 0, unhealthy: 1, latency: 60);

            var result = await pass.RunAsync(Tenant, HealthProbeScope.Site, 7);
            _output.WriteLine($"[result] {result}");

            var hour = rollups.Rows.Values.Single(row => row.Window == HealthRollupWindow.Hour);
            _output.WriteLine($"[hour] checks={hour.Checks} healthy={hour.Healthy} avg={hour.AverageLatencyMs} max={hour.MaxLatencyMs} avail={hour.AvailabilityPercent}");

            Assert.Equal(Noon, hour.PeriodStartUtc);
            Assert.Equal(3, hour.Checks);
            Assert.Equal(2, hour.Healthy);
            Assert.Equal(1, hour.Unhealthy);
            Assert.Equal(30d, hour.AverageLatencyMs);          // (10 + 20 + 60) / 3
            Assert.Equal(60d, hour.MaxLatencyMs);
            Assert.Equal(66.67, hour.AvailabilityPercent);     // 2 of 3
        }

        [Fact]
        public async Task Hours_fold_into_a_day_and_the_day_agrees_with_the_hours()
        {
            var rollups = new InMemoryHealthRollups();
            var pass = new HealthRollupPass(rollups, new HealthNoOpUnitOfWork(), new Clock(Noon.AddDays(1)));

            Minute(rollups, Noon, healthy: 1, unhealthy: 0, latency: 10);
            Minute(rollups, Noon.AddHours(3), healthy: 0, unhealthy: 1, latency: 50);

            await pass.RunAsync(Tenant, HealthProbeScope.Site, 7);

            var day = rollups.Rows.Values.Single(row => row.Window == HealthRollupWindow.Day);
            _output.WriteLine($"[day] checks={day.Checks} healthy={day.Healthy} avail={day.AvailabilityPercent}");

            Assert.Equal(Noon.Date, day.PeriodStartUtc);
            Assert.Equal(2, day.Checks);
            Assert.Equal(1, day.Healthy);
            Assert.Equal(50d, day.AvailabilityPercent);
        }

        [Fact]
        public async Task Running_the_pass_twice_changes_nothing()
        {
            // The property that lets a missed run cost only freshness and a double run cost nothing: the pass
            // RECOMPUTES each window from the rows beneath it and Sets the result, rather than accumulating.
            var rollups = new InMemoryHealthRollups();
            var pass = new HealthRollupPass(rollups, new HealthNoOpUnitOfWork(), new Clock(Noon.AddHours(1)));

            Minute(rollups, Noon, healthy: 1, unhealthy: 0, latency: 10);
            Minute(rollups, Noon.AddMinutes(1), healthy: 1, unhealthy: 0, latency: 30);

            await pass.RunAsync(Tenant, HealthProbeScope.Site, 7);
            var first = Snapshot(rollups);

            await pass.RunAsync(Tenant, HealthProbeScope.Site, 7);
            var second = Snapshot(rollups);

            _output.WriteLine($"[first ] {first}");
            _output.WriteLine($"[second] {second}");
            Assert.Equal(first, second);

            static string Snapshot(InMemoryHealthRollups rollups) => string.Join(" | ", rollups.Rows.Values
                .OrderBy(row => row.Window).ThenBy(row => row.PeriodStartUtc)
                .Select(row => $"{row.Window}@{row.PeriodStartUtc:HH:mm}:{row.Checks}/{row.Healthy}/{row.AverageLatencyMs}"));
        }

        [Fact]
        public async Task The_sweep_removes_minute_rows_the_trim_on_write_could_not_reach()
        {
            // If probing stops for a day, the trim has no writes to piggy-back on and its rows are stranded. This
            // is the safety net for that - not the mechanism, which is the trim.
            var rollups = new InMemoryHealthRollups();
            var pass = new HealthRollupPass(rollups, new HealthNoOpUnitOfWork(), new Clock(Noon));

            Minute(rollups, Noon.AddHours(-72), healthy: 1, unhealthy: 0, latency: 10);   // stranded
            Minute(rollups, Noon.AddHours(-1), healthy: 1, unhealthy: 0, latency: 10);    // live

            var result = await pass.RunAsync(Tenant, HealthProbeScope.Site, 7);
            _output.WriteLine($"[result] {result}");

            Assert.Equal(1, result.MinutesSwept);
            var minutes = rollups.Rows.Values.Where(row => row.Window == HealthRollupWindow.Minute).ToList();
            Assert.Single(minutes);
            Assert.Equal(Noon.AddHours(-1), minutes[0].PeriodStartUtc);
        }

        [Fact]
        public async Task A_site_with_nothing_recorded_writes_nothing()
        {
            var rollups = new InMemoryHealthRollups();
            var pass = new HealthRollupPass(rollups, new HealthNoOpUnitOfWork(), new Clock(Noon));

            var result = await pass.RunAsync(Tenant, HealthProbeScope.Site, 7);
            _output.WriteLine($"[result] {result}");

            Assert.Equal(0, result.HoursWritten);
            Assert.Equal(0, result.DaysWritten);
            Assert.Empty(rollups.Rows);
        }

        private static void Minute(InMemoryHealthRollups rollups, DateTime at, int healthy, int unhealthy, double latency)
        {
            var row = HealthRollupAggregate.For(Tenant, HealthProbeScope.Site, 7, Subject, HealthRollupWindow.Minute, at);
            row.Set(healthy + unhealthy, healthy, 0, unhealthy, latency, latency,
                Math.Round((double)healthy / (healthy + unhealthy) * 100d, 2));
            rollups.Rows[row.Id] = row;
        }
    }
}
