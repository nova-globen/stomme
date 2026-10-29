#nullable enable
using System;
using System.Linq;
using System.Reflection;
using Stomme.Backup;
using Stomme.Modules.Backup.Domain;
using Xunit;

namespace Stomme.Tests.Backup
{
    /// <summary>
    /// The catalogue's rules: the snapshot's life (the predecessor platform's <c>BackupSnapshotTests</c>, ported by name
    /// in the module README, with <c>FailRestore</c> INVERTED - a failed restore is <c>RestoreFailed</c>, not Available), the
    /// staged restore D106 adds, the schedule's due rule, and the value objects (the predecessor platform's <c>BackupValueObjectTests</c>).
    /// </summary>
    public class BackupSnapshotAggregateTests
    {
        private static readonly DateTime Now = new(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc);
        private static readonly Checksum Sum = Checksum.Compute(new byte[] { 1, 2, 3 });

        private static BackupSnapshotAggregate Started() => BackupSnapshotAggregate.Start(Guid.NewGuid(), RetentionTag.Manual, "10.2.3", Now);

        private static BackupSnapshotAggregate Available()
        {
            var snapshot = Started();
            snapshot.MarkCompleted(Sum, 4096, Now.AddMinutes(1));
            return snapshot;
        }

        [Fact]
        public void Start_begins_Creating_with_no_checksum_and_a_file_name_of_its_own()
        {
            var snapshot = Started();
            Assert.Equal(BackupSnapshotStatus.Creating, snapshot.Status);
            Assert.Null(snapshot.GetChecksum());
            Assert.Equal($"{snapshot.Id:N}.stbak", snapshot.StorageLocation.Value); // BL-261; a row stored as .dcbak keeps its name
            Assert.Equal(("10.2.3", Now, 0L), (snapshot.FrameworkVersion, snapshot.CreatedOnUtc, snapshot.SizeBytes));
            Assert.Throws<ArgumentNullException>(() => BackupSnapshotAggregate.Start(Guid.NewGuid(), null!, "10.2.3", Now));
            Assert.Throws<ArgumentException>(() => BackupSnapshotAggregate.Start(Guid.NewGuid(), RetentionTag.Manual, " ", Now));
            Assert.Throws<ArgumentException>(() => BackupSnapshotAggregate.Start(Guid.NewGuid(), RetentionTag.Manual, "10.2.3", DateTime.Now));
        }

        [Fact]
        public void MarkCompleted_moves_to_Available_once()
        {
            var snapshot = Available();
            Assert.Equal(BackupSnapshotStatus.Available, snapshot.Status);
            Assert.Equal(Sum, snapshot.GetChecksum());
            Assert.Equal((4096L, (DateTime?)Now.AddMinutes(1)), (snapshot.SizeBytes, snapshot.CompletedOnUtc));
            Assert.Throws<InvalidOperationException>(() => snapshot.MarkCompleted(Sum, 4096, Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => Started().MarkCompleted(Sum, 0, Now));
        }

        [Fact]
        public void Fail_while_Creating_records_the_reason_and_cannot_follow_completion()
        {
            var snapshot = Started();
            snapshot.Fail("IOException", Now);
            Assert.Equal((BackupSnapshotStatus.Failed, "IOException"), (snapshot.Status, snapshot.FailureReason));
            Assert.Throws<InvalidOperationException>(() => Available().Fail("IOException", Now));
            Assert.Throws<ArgumentException>(() => Started().Fail(" ", Now));
            var longReason = new string('x', 2000);
            var clipped = Started();
            clipped.Fail(longReason, Now);
            Assert.Equal(BackupSnapshotAggregate.MaximumFailureLength, clipped.FailureReason.Length);
        }

        [Fact]
        public void EnsureRestorable_needs_Available_and_a_matching_checksum()
        {
            Assert.Throws<InvalidOperationException>(() => Started().EnsureRestorable(Sum));
            var available = Available();
            available.EnsureRestorable(Checksum.Create(Sum.Value.ToUpperInvariant()));
            var mismatch = Assert.Throws<InvalidOperationException>(() => available.EnsureRestorable(Checksum.Compute(new byte[] { 9 })));
            Assert.Contains("corrupt or was replaced", mismatch.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_restore_is_staged_begun_and_completed_back_to_Available()
        {
            var snapshot = Available();
            snapshot.Stage(Sum, Now.AddHours(1));
            Assert.Equal((BackupSnapshotStatus.Staged, (DateTime?)Now.AddHours(1)), (snapshot.Status, snapshot.StagedOnUtc));
            Assert.False(snapshot.CanBeDeleted());
            Assert.Throws<InvalidOperationException>(() => snapshot.Stage(Sum, Now));
            snapshot.BeginRestore();
            Assert.Equal(BackupSnapshotStatus.Restoring, snapshot.Status);
            Assert.False(snapshot.CanBeDeleted());
            snapshot.CompleteRestore(Now.AddHours(2));
            Assert.Equal((BackupSnapshotStatus.Available, (DateTime?)null, (DateTime?)Now.AddHours(2)), (snapshot.Status, snapshot.StagedOnUtc, snapshot.LastRestoredOnUtc));
            Assert.True(snapshot.CanBeDeleted());
        }

        // INVERTED from the predecessor platform's FailRestore_LeavesSnapshotAvailable: a restore that did not finish is shown, not hidden.
        [Fact]
        public void A_failed_restore_is_RestoreFailed_and_the_snapshot_stays_restorable()
        {
            var snapshot = Available();
            snapshot.Stage(Sum, Now);
            snapshot.BeginRestore();
            snapshot.FailRestore("the Users part refused its count check");
            Assert.Equal((BackupSnapshotStatus.RestoreFailed, "the Users part refused its count check"), (snapshot.Status, snapshot.FailureReason));
            snapshot.Stage(Sum, Now.AddHours(1));                                   // the archive is untouched - try again
            Assert.Equal((BackupSnapshotStatus.Staged, string.Empty), (snapshot.Status, snapshot.FailureReason));
        }

        [Fact]
        public void The_restore_steps_refuse_the_wrong_status()
        {
            Assert.Throws<InvalidOperationException>(() => Available().BeginRestore());
            Assert.Throws<InvalidOperationException>(() => Available().CompleteRestore(Now));
            Assert.Throws<InvalidOperationException>(() => Available().FailRestore("x"));
            Assert.Throws<InvalidOperationException>(() => Available().Unstage());
            var staged = Available();
            staged.Stage(Sum, Now);
            staged.Unstage();
            Assert.Equal((BackupSnapshotStatus.Available, (DateTime?)null), (staged.Status, staged.StagedOnUtc));
        }

        [Fact]
        public void An_imported_archive_is_Available_at_once_and_keeps_the_instant_it_was_taken()
        {
            var taken = Now.AddDays(-3);
            var imported = BackupSnapshotAggregate.Imported(Guid.NewGuid(), "10.2.3", taken, Sum, 2048, Now);
            Assert.Equal((BackupSnapshotStatus.Available, taken, (DateTime?)Now), (imported.Status, imported.CreatedOnUtc, imported.CompletedOnUtc));
            Assert.Equal(RetentionTag.Imported, imported.RetentionTag);
        }

        [Fact]
        public void The_catalogue_is_excluded_from_the_snapshot_and_is_not_tenant_owned()
        {
            foreach (var type in new[] { typeof(BackupSnapshotAggregate), typeof(BackupScheduleAggregate) })
            {
                Assert.False(string.IsNullOrWhiteSpace(type.GetCustomAttribute<BackupExcludedAttribute>()?.Reason));
                Assert.DoesNotContain(type.GetInterfaces(), i => i.Name == "ITenantOwned");
            }
        }

        [Fact]
        public void A_schedule_is_due_when_enabled_and_an_interval_has_passed()
        {
            var schedule = BackupScheduleAggregate.Create(intervalHours: 24, retentionDays: 30, keepLast: 3, enabled: true, Now);
            Assert.True(schedule.IsDue(Now));
            schedule.MarkRun(Now);
            Assert.False(schedule.IsDue(Now.AddHours(23)));
            Assert.True(schedule.IsDue(Now.AddHours(24)));
            schedule.Redefine(24, 30, 3, enabled: false);
            Assert.False(schedule.IsDue(Now.AddDays(10)));
            Assert.Throws<ArgumentOutOfRangeException>(() => BackupScheduleAggregate.Create(0, 30, 3, true, Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => BackupScheduleAggregate.Create(24, 0, 3, true, Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => BackupScheduleAggregate.Create(24, 30, 0, true, Now));
            Assert.Throws<ArgumentOutOfRangeException>(() => BackupScheduleAggregate.Create(BackupScheduleAggregate.MaximumIntervalHours + 1, 30, 3, true, Now));
        }

        // ---- value objects (the predecessor platform's BackupValueObjectTests) -----------------------------------------------------------------

        [Fact]
        public void Checksum_is_64_lower_hex_and_compares_case_blind_on_input()
        {
            var computed = Checksum.Compute(new byte[] { 1, 2, 3 });
            Assert.Equal(64, computed.Value.Length);
            Assert.Equal(computed.Value.ToLowerInvariant(), computed.Value);
            Assert.Equal(computed, Checksum.Compute(new byte[] { 1, 2, 3 }));
            Assert.Equal(computed, Checksum.Create(computed.Value.ToUpperInvariant()));
            Assert.Throws<ArgumentException>(() => Checksum.Create("abc"));
            Assert.Throws<ArgumentException>(() => Checksum.Create(new string('g', 64)));
            Assert.Throws<ArgumentException>(() => Checksum.Create(" "));
        }

        [Theory]
        [InlineData("../escape.dcbak")]
        [InlineData("..")]
        [InlineData("a/b.dcbak")]
        [InlineData("a\\b.dcbak")]
        [InlineData("C:x.dcbak")]
        [InlineData(".hidden")]
        [InlineData("a..b")]
        [InlineData(" ")]
        public void StorageLocation_is_one_safe_file_name(string name) =>
            Assert.Throws<ArgumentException>(() => StorageLocation.Create(name));

        [Fact]
        public void StorageLocation_trims_and_compares_by_value()
        {
            Assert.Equal(StorageLocation.Create("a.dcbak"), StorageLocation.Create("  a.dcbak "));
            Assert.Throws<ArgumentException>(() => StorageLocation.Create(new string('a', StorageLocation.MaxLength + 1)));
        }

        [Fact]
        public void RetentionTag_normalises_to_lower_case_and_knows_indefinite()
        {
            Assert.Equal(RetentionTag.PreRestore, RetentionTag.Create(" Pre-Restore "));
            Assert.True(RetentionTag.Create("INDEFINITE").KeptForever);
            Assert.False(RetentionTag.Scheduled.KeptForever);
            Assert.Throws<ArgumentException>(() => RetentionTag.Create(" "));
            Assert.Throws<ArgumentException>(() => RetentionTag.Create("keep me"));
        }
    }
}
