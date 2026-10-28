#nullable enable
using System;
using Stomme.Export;
using Stomme.Modules.DataExport.Domain;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.DataExport
{
    /// <summary>BL-95: a recurring export's rules - a five-field UTC cron at least 15 minutes apart, missed runs collapsed into one, paused never catching up.</summary>
    public sealed class ExportScheduleAggregateTests
    {
        private static readonly TenantId Tenant = TenantId.From(Guid.Parse("7e6a1d3c-0000-4000-8000-000000000001"));

        private static readonly DateTime Monday0500 = new(2026, 10, 5, 5, 0, 0, DateTimeKind.Utc);

        private static ExportScheduleAggregate Daily0600(DateTime now) => ExportScheduleAggregate.Create(
            Guid.NewGuid(), Tenant, 1, 7, " Morning rows ", "test.rows", ExportFormat.Csv, null, "{}", "0 6 * * *", now);

        [Theory]
        [InlineData("0 6 * * *", null)]
        [InlineData("*/15 * * * *", null)]
        [InlineData("0 6 * * 1", null)]
        [InlineData("*/5 * * * *", "at least 15 minutes")]
        [InlineData("* * * * *", "at least 15 minutes")]
        [InlineData("0 6 * *", "not a five-field cron")]
        [InlineData("0 0 6 * * *", "not a five-field cron")]
        [InlineData("", "five fields")]
        public void The_cron_is_five_fields_in_UTC_and_runs_at_least_15_minutes_apart(string cron, string? refusal)
        {
            var problem = ExportScheduleAggregate.CronProblem(cron);
            if (refusal is null)
            {
                Assert.Null(problem);
            }
            else
            {
                Assert.Contains(refusal, problem ?? "", StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void A_new_schedule_runs_at_its_next_occurrence_and_is_not_due_before_it()
        {
            var schedule = Daily0600(Monday0500);

            Assert.Equal("Morning rows", schedule.Name);
            Assert.Equal(Monday0500.AddHours(1), schedule.NextRunOnUtc);
            Assert.False(schedule.IsDue(Monday0500.AddMinutes(59)));
            Assert.True(schedule.IsDue(Monday0500.AddHours(1)));
        }

        [Fact]
        public void Missed_occurrences_collapse_into_one_run_and_the_next_is_after_now()
        {
            var schedule = Daily0600(Monday0500);
            var threeDaysLate = Monday0500.AddDays(3).AddHours(2); // Thursday 07:00, three 06:00s missed
            var job = Guid.NewGuid();

            schedule.RecordRun(job, threeDaysLate);

            Assert.Equal(job, schedule.LastJobId);
            Assert.Equal(threeDaysLate, schedule.LastRunOnUtc);
            Assert.Equal(new DateTime(2026, 10, 9, 6, 0, 0, DateTimeKind.Utc), schedule.NextRunOnUtc);
            Assert.False(schedule.IsDue(threeDaysLate));
            Assert.Throws<InvalidOperationException>(() => schedule.RecordRun(Guid.NewGuid(), threeDaysLate));
        }

        [Fact]
        public void A_paused_schedule_is_never_due_and_resumes_from_the_next_occurrence_without_catching_up()
        {
            var schedule = Daily0600(Monday0500);
            schedule.Pause(ExportSchedulePauseReason.OwnerNotAllowed, Monday0500);

            Assert.True(schedule.IsPaused);
            Assert.Null(schedule.NextRunOnUtc);
            Assert.False(schedule.IsDue(Monday0500.AddDays(10)));

            var later = Monday0500.AddDays(10).AddHours(2);
            schedule.Resume(later);
            Assert.False(schedule.IsPaused);
            Assert.Equal(ExportSchedulePauseReason.None, schedule.PauseReason);
            Assert.Equal(new DateTime(2026, 10, 16, 6, 0, 0, DateTimeKind.Utc), schedule.NextRunOnUtc);
        }

        [Fact]
        public void A_take_over_changes_who_every_later_run_is_requested_as()
        {
            var schedule = Daily0600(Monday0500);
            schedule.TakeOver(42);
            Assert.Equal(42, schedule.OwnerUserId);
        }

        [Fact]
        public void Creating_refuses_what_the_request_path_would_and_a_local_time()
        {
            Assert.Throws<ArgumentException>(() => ExportScheduleAggregate.Create(Guid.NewGuid(), Tenant, 1, 7, "x", "Test.Rows", ExportFormat.Csv, null, "{}", "0 6 * * *", Monday0500));
            Assert.Throws<ArgumentException>(() => ExportScheduleAggregate.Create(Guid.NewGuid(), Tenant, 1, 7, "x", "test.rows", ExportFormat.Csv, null, "{}", "*/5 * * * *", Monday0500));
            Assert.Throws<ArgumentException>(() => ExportScheduleAggregate.Create(Guid.NewGuid(), Tenant, 1, 7, " ", "test.rows", ExportFormat.Csv, null, "{}", "0 6 * * *", Monday0500));
            Assert.Throws<ArgumentException>(() => ExportScheduleAggregate.Create(Guid.NewGuid(), Tenant, 1, 7, "x", "test.rows", ExportFormat.Csv, null, "{}", "0 6 * * *", DateTime.SpecifyKind(Monday0500, DateTimeKind.Local)));
        }
    }
}
