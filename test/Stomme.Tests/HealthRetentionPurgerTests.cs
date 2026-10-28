#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Health;
using Stomme.Health.Persistence;
using Stomme.Modules.Health.Application;
using Stomme.Modules.Health.Domain;
using Stomme.Modules.Health.Infrastructure;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Xunit;
using Xunit.Abstractions;

namespace Stomme.Tests
{
    /// <summary>
    /// What the health retention pass deletes, and — more importantly — what it must not (D104 binding item 11).
    /// </summary>
    public class HealthRetentionPurgerTests
    {
        private readonly ITestOutputHelper _output;

        public HealthRetentionPurgerTests(ITestOutputHelper output) => _output = output;

        private static readonly TenantId Tenant = TenantId.From(Guid.Parse("44444444-4444-4444-4444-444444444444"));
        private static readonly HealthSubject Subject = HealthSubject.For("stomme", "store");

        [Fact]
        public async Task It_purges_old_hour_and_day_rows_and_leaves_the_live_minute_series_alone()
        {
            var rollups = new InMemoryHealthRollups();
            var purger = new HealthRetentionPurger(rollups, new HealthNoOpUnitOfWork());
            var now = DateTime.UtcNow;

            Add(rollups, HealthRollupWindow.Hour, now.AddDays(-40));   // old   -> goes
            Add(rollups, HealthRollupWindow.Day, now.AddDays(-40));    // old   -> goes
            Add(rollups, HealthRollupWindow.Hour, now.AddDays(-2));    // fresh -> stays
            Add(rollups, HealthRollupWindow.Day, now.AddDays(-1));     // fresh -> stays

            // The live series is trimmed ON WRITE by the probe pass, which is what keeps it bounded even when an
            // operator disables PurgeJob. Purging it here as well would make that guarantee depend on the job.
            Add(rollups, HealthRollupWindow.Minute, now.AddDays(-40));

            var purged = await purger.PurgeAsync(siteId: 7, days: 30);
            _output.WriteLine($"[purged] {purged}; left {rollups.Rows.Count}");

            Assert.Equal(2, purged);
            Assert.Equal(3, rollups.Rows.Count);
            Assert.Contains(rollups.Rows.Values, row => row.Window == HealthRollupWindow.Minute);
            Assert.DoesNotContain(rollups.Rows.Values, row => row.Window != HealthRollupWindow.Minute && row.PeriodStartUtc < now.AddDays(-30));
        }

        [Fact]
        public async Task It_never_touches_another_sites_rows()
        {
            var rollups = new InMemoryHealthRollups();
            var purger = new HealthRetentionPurger(rollups, new HealthNoOpUnitOfWork());
            var old = DateTime.UtcNow.AddDays(-90);

            Add(rollups, HealthRollupWindow.Day, old, siteId: 7);
            Add(rollups, HealthRollupWindow.Day, old, siteId: 8);

            var purged = await purger.PurgeAsync(siteId: 7, days: 30);
            _output.WriteLine($"[purged] {purged}; left {string.Join(", ", rollups.Rows.Values.Select(row => row.SiteId))}");

            Assert.Equal(1, purged);
            Assert.Equal(8, Assert.Single(rollups.Rows.Values).SiteId);
        }

        [Fact]
        public async Task The_retention_is_clamped_so_zero_days_does_not_delete_the_history_the_page_shows()
        {
            // Zero is meaningful for the framework's own passes ("purge everything older than now") and exactly
            // wrong here: it would empty the admin page every night. The clamp lives in the module, because only
            // the module knows what its data is worth.
            var rollups = new InMemoryHealthRollups();
            var purger = new HealthRetentionPurger(rollups, new HealthNoOpUnitOfWork());

            // An HOUR row at "now", not a Day row at "two hours ago". The first version of this test used the
            // latter and went red for a good reason: For() SNAPS a Day row back to that day's midnight, so
            // "two hours ago" becomes the PREVIOUS midnight whenever the clock is in the small hours - which, at
            // 00:30 UTC, is more than the clamped one-day retention. The purge was right and the input was wrong.
            Add(rollups, HealthRollupWindow.Hour, DateTime.UtcNow);

            var purged = await purger.PurgeAsync(siteId: 7, days: 0);
            _output.WriteLine($"[purged with days=0] {purged}; left {rollups.Rows.Count}");

            Assert.Equal(0, purged);
            Assert.Single(rollups.Rows);

            // And the ceiling holds too: a value past a year is a year.
            Assert.Equal(HealthSettings.MinimumRetentionDays, HealthSettings.ClampRetention(0));
            Assert.Equal(HealthSettings.MaximumRetentionDays, HealthSettings.ClampRetention(10_000));
            Assert.Equal(30, HealthSettings.ClampRetention(30));
        }

        [Fact]
        public void It_declares_the_setting_and_default_the_job_reads()
        {
            var purger = new HealthRetentionPurger(new InMemoryHealthRollups(), new HealthNoOpUnitOfWork());

            // The JOB owns reading and defaulting the setting, so one module cannot answer "what does an
            // unparseable retention mean" differently from the framework.
            Assert.Equal("HealthRetention", purger.SettingKey);
            Assert.Equal(30, purger.DefaultDays);
            Assert.False(string.IsNullOrWhiteSpace(purger.DisplayName));
        }

        // ---- helpers ---------------------------------------------------------------------------------------

        private static void Add(InMemoryHealthRollups rollups, HealthRollupWindow window, DateTime at, int siteId = 7)
        {
            var row = HealthRollupAggregate.For(Tenant, HealthProbeScope.Site, siteId, Subject, window, at);
            row.Set(1, 1, 0, 0, 1, 1, 100);
            rollups.Rows[row.Id] = row;
        }
    }
}