#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Stomme.Backup;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Backup.Domain
{
    public enum BackupSnapshotStatus
    {
        Creating = 0,
        Available = 1,
        Staged = 2,
        Restoring = 3,
        RestoreFailed = 4,
        Failed = 5,
    }

    /// <summary>
    /// Where a snapshot's archive lives in the archive store: a single file NAME, never a path - so nothing a row holds can
    /// point outside the store's root (D106 item 9).
    /// </summary>
    public sealed class StorageLocation : ValueObject, IStringValueObject
    {
        public const int MaxLength = 128;

        private StorageLocation(string value) => Value = value;

        public string Value { get; }

        public static StorageLocation Create(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                throw new ArgumentException("A storage location is required.", nameof(value));
            }

            if (trimmed.Length > MaxLength || !IsFileName(trimmed))
            {
                throw new ArgumentException($"A storage location is one file name of letters, digits, '.', '-' and '_', at most {MaxLength} characters.", nameof(value));
            }

            return new StorageLocation(trimmed);
        }

        /// <summary>A Stomme backup archive's extension (BL-261; since 10.75.0).</summary>
        public const string ArchiveExtension = ".stbak";

        /// <summary>
        /// The extension archives had before 10.75.0 (BL-261: it carried the old product name). Still READ: every snapshot row
        /// keeps the name it was stored under, so an archive taken earlier is listed, restored, downloaded and retained as before.
        /// </summary>
        public const string LegacyArchiveExtension = ".dcbak";

        public static StorageLocation ForSnapshot(Guid id) => Create($"{id:N}{ArchiveExtension}");

        internal static bool IsFileName(string name) =>
            name.Length > 0 && char.IsAsciiLetterOrDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') && !name.Contains("..", StringComparison.Ordinal);

        protected override IEnumerable<object> GetEqualityComponents()
        {
            yield return Value;
        }

        public override string ToString() => Value;
    }

    /// <summary>A SHA-256 of an archive file: 64 hex digits, normalised to lower case.</summary>
    public sealed class Checksum : ValueObject, IStringValueObject
    {
        public const int MaxLength = 64;

        private Checksum(string value) => Value = value;

        public string Value { get; }

        public static Checksum Create(string value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Length != MaxLength)
            {
                throw new ArgumentException($"A SHA-256 checksum is {MaxLength} hex characters.", nameof(value));
            }

            if (!normalized.All(char.IsAsciiHexDigitLower))
            {
                throw new ArgumentException("A SHA-256 checksum holds only hexadecimal characters.", nameof(value));
            }

            return new Checksum(normalized);
        }

        public static Checksum Compute(ReadOnlySpan<byte> bytes) => new(Convert.ToHexStringLower(SHA256.HashData(bytes)));

        public static async System.Threading.Tasks.Task<Checksum> ComputeAsync(Stream stream, System.Threading.CancellationToken cancellationToken = default) =>
            new(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)));

        protected override IEnumerable<object> GetEqualityComponents()
        {
            yield return Value;
        }

        public override string ToString() => Value;
    }

    /// <summary>
    /// Why a snapshot is kept: <c>manual</c>, <c>scheduled</c>, <c>imported</c>, <c>pre-restore</c> (the automatic safety
    /// snapshot), <c>pre-upgrade</c> (taken before a framework upgrade, BL-172), or <c>indefinite</c> - which retention never removes. Lower case; letters, digits and '-'.
    /// </summary>
    public sealed class RetentionTag : ValueObject, IStringValueObject
    {
        public const int MaxLength = 32;

        public static readonly RetentionTag Manual = new("manual");
        public static readonly RetentionTag Scheduled = new("scheduled");
        public static readonly RetentionTag Imported = new("imported");
        public static readonly RetentionTag PreRestore = new("pre-restore");
        public static readonly RetentionTag PreUpgrade = new("pre-upgrade");
        public static readonly RetentionTag Indefinite = new("indefinite");

        private RetentionTag(string value) => Value = value;

        public string Value { get; }

        public bool KeptForever => Value == Indefinite.Value;

        public static RetentionTag Create(string value)
        {
            var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.Length == 0)
            {
                throw new ArgumentException("A retention tag is required.", nameof(value));
            }

            if (normalized.Length > MaxLength || !normalized.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                throw new ArgumentException($"A retention tag is letters, digits and '-', at most {MaxLength} characters.", nameof(value));
            }

            return new RetentionTag(normalized);
        }

        protected override IEnumerable<object> GetEqualityComponents()
        {
            yield return Value;
        }

        public override string ToString() => Value;
    }

    /// <summary>
    /// One snapshot of the installation (BL-81, D106 item 6) and the life of its archive: created, restored, retired.
    /// INSTALLATION-wide - deliberately NOT tenant-owned: a snapshot holds every tenant. Excluded from the snapshot itself,
    /// so a restore never rewrites the rows that drive it.
    /// </summary>
    /// <remarks>
    /// Creating -&gt; Available | Failed. A restore: Available -&gt; Staged (pre-flight passed, waiting for the restart) -&gt;
    /// Restoring (the boot phase is applying it) -&gt; Available, or RestoreFailed. the predecessor platform returned a failed restore to
    /// Available; here it is RestoreFailed - a restore that did not finish is a fact the operator must see (D106 item 7).
    /// The archive is untouched by a restore, so a RestoreFailed snapshot is still restorable.
    /// </remarks>
    [BackupExcluded("the catalogue: a restore must never rewrite the rows that drive it")]
    public sealed class BackupSnapshotAggregate : AggregateRoot<Guid>
    {
        public const int MaximumFailureLength = 512;
        public const int MaximumVersionLength = 64;
        public const int MaximumArchiveStoreLength = 16;

        private BackupSnapshotAggregate()
        {
            StorageLocation = null!;
            RetentionTag = null!;
            FrameworkVersion = string.Empty;
            ArchiveSha256 = string.Empty;
            FailureReason = string.Empty;
        }

        public BackupSnapshotStatus Status { get; private set; }

        public StorageLocation StorageLocation { get; private set; }

        public RetentionTag RetentionTag { get; private set; }

        /// <summary>The Stomme version the snapshot was taken on - a restore needs the same one.</summary>
        public string FrameworkVersion { get; private set; }

        /// <summary>The archive file's SHA-256 once Available; empty before. A corruption check, not a security one (refutation 16).</summary>
        public string ArchiveSha256 { get; private set; }

        public long SizeBytes { get; private set; }

        public DateTime CreatedOnUtc { get; private set; }

        public DateTime? CompletedOnUtc { get; private set; }

        public DateTime? StagedOnUtc { get; private set; }

        public DateTime? LastRestoredOnUtc { get; private set; }

        /// <summary>Why creation or the last restore failed - an exception TYPE or a stated reason, never a raw message.</summary>
        public string FailureReason { get; private set; }

        /// <summary>
        /// The archive store that holds the archive (BL-169): <c>Local</c> or <c>S3</c>. Null on a row written before the field
        /// existed, which is Local - the only store there was.
        /// </summary>
        public string? ArchiveStore { get; private set; }

        /// <summary>A method, not a property: the stores map every public property, and this one is derived.</summary>
        public Checksum? GetChecksum() => ArchiveSha256.Length == 0 ? null : Checksum.Create(ArchiveSha256);

        public static BackupSnapshotAggregate Start(Guid id, RetentionTag retentionTag, string frameworkVersion, DateTime nowUtc, string archiveStore = "Local")
        {
            ArgumentNullException.ThrowIfNull(retentionTag);
            return new BackupSnapshotAggregate
            {
                Id = id,
                Status = BackupSnapshotStatus.Creating,
                StorageLocation = StorageLocation.ForSnapshot(id),
                RetentionTag = retentionTag,
                FrameworkVersion = Version(frameworkVersion),
                CreatedOnUtc = Utc(nowUtc),
                ArchiveStore = Store(archiveStore),
            };
        }

        /// <summary>
        /// A row for an archive brought in from elsewhere (<c>ImportArchive</c>) - created Available, AFTER its GCM verified.
        /// The archive keeps the instant it was taken; the row records it.
        /// </summary>
        public static BackupSnapshotAggregate Imported(Guid id, string frameworkVersion, DateTime takenOnUtc, Checksum checksum, long sizeBytes, DateTime nowUtc, string archiveStore = "Local")
        {
            var snapshot = Start(id, RetentionTag.Imported, frameworkVersion, takenOnUtc, archiveStore);
            snapshot.MarkCompleted(checksum, sizeBytes, nowUtc);
            return snapshot;
        }

        public void MarkCompleted(Checksum checksum, long sizeBytes, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(checksum);
            Require(BackupSnapshotStatus.Creating, "completed");
            if (sizeBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeBytes), "An archive has bytes.");
            }

            Status = BackupSnapshotStatus.Available;
            ArchiveSha256 = checksum.Value;
            SizeBytes = sizeBytes;
            CompletedOnUtc = Utc(nowUtc);
        }

        public void Fail(string reason, DateTime nowUtc)
        {
            Require(BackupSnapshotStatus.Creating, "failed");
            Status = BackupSnapshotStatus.Failed;
            FailureReason = Reason(reason);
            CompletedOnUtc = Utc(nowUtc);
        }

        /// <summary>
        /// Throws unless this snapshot can be restored from an archive whose file hashes to <paramref name="archiveChecksum"/>:
        /// Available (or RestoreFailed - the archive is untouched) with a matching checksum.
        /// </summary>
        public void EnsureRestorable(Checksum archiveChecksum)
        {
            ArgumentNullException.ThrowIfNull(archiveChecksum);
            if (Status is not (BackupSnapshotStatus.Available or BackupSnapshotStatus.RestoreFailed))
            {
                throw new InvalidOperationException($"Only an available snapshot can be restored (this one is {Status}).");
            }

            if (GetChecksum() is not { } recorded || !recorded.Equals(archiveChecksum))
            {
                throw new InvalidOperationException("The archive does not match this snapshot's checksum: it is corrupt or was replaced.");
            }
        }

        /// <summary>The pre-flight passed: the restore waits for the restart that applies it.</summary>
        public void Stage(Checksum archiveChecksum, DateTime nowUtc)
        {
            EnsureRestorable(archiveChecksum);
            Status = BackupSnapshotStatus.Staged;
            StagedOnUtc = Utc(nowUtc);
            FailureReason = string.Empty;
        }

        /// <summary>A staged restore withdrawn before the restart.</summary>
        public void Unstage()
        {
            Require(BackupSnapshotStatus.Staged, "unstaged");
            Status = BackupSnapshotStatus.Available;
            StagedOnUtc = null;
        }

        public void BeginRestore()
        {
            Require(BackupSnapshotStatus.Staged, "restored");
            Status = BackupSnapshotStatus.Restoring;
        }

        public void CompleteRestore(DateTime nowUtc)
        {
            Require(BackupSnapshotStatus.Restoring, "marked restored");
            Status = BackupSnapshotStatus.Available;
            StagedOnUtc = null;
            LastRestoredOnUtc = Utc(nowUtc);
        }

        public void FailRestore(string reason)
        {
            Require(BackupSnapshotStatus.Restoring, "marked as a failed restore");
            Status = BackupSnapshotStatus.RestoreFailed;
            StagedOnUtc = null;
            FailureReason = Reason(reason);
        }

        /// <summary>Retention or the operator may remove a snapshot only when nothing is using it.</summary>
        public bool CanBeDeleted() => Status is BackupSnapshotStatus.Available or BackupSnapshotStatus.Failed or BackupSnapshotStatus.RestoreFailed;

        private void Require(BackupSnapshotStatus expected, string verb)
        {
            if (Status != expected)
            {
                throw new InvalidOperationException($"Only a {expected} snapshot can be {verb} (this one is {Status}).");
            }
        }

        private static string Reason(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("A failure states its reason.", nameof(reason));
            }

            return reason.Length <= MaximumFailureLength ? reason : reason[..MaximumFailureLength];
        }

        private static string Store(string archiveStore)
        {
            var trimmed = (archiveStore ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaximumArchiveStoreLength || !trimmed.All(char.IsAsciiLetterOrDigit))
            {
                throw new ArgumentException($"An archive store is named by letters and digits, at most {MaximumArchiveStoreLength}.", nameof(archiveStore));
            }

            return trimmed;
        }

        private static string Version(string frameworkVersion)
        {
            if (string.IsNullOrWhiteSpace(frameworkVersion) || frameworkVersion.Length > MaximumVersionLength)
            {
                throw new ArgumentException("A snapshot names the framework version it was taken on.", nameof(frameworkVersion));
            }

            return frameworkVersion;
        }

        private static DateTime Utc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => throw new ArgumentException("An instant is UTC.", nameof(value)),
        };
    }

    /// <summary>
    /// A backup schedule (D106 item 8): how often, how long to keep, how many at least. It holds NO secret - the scheduled
    /// run's passphrase is operator configuration (<c>Backup:Schedule:Passphrase</c>), never a row.
    /// </summary>
    [BackupExcluded("the catalogue: schedules drive the job that takes snapshots, and must survive a restore of an older one")]
    public sealed class BackupScheduleAggregate : AggregateRoot<Guid>
    {
        public const int MaximumIntervalHours = 24 * 366;

        private BackupScheduleAggregate()
        {
        }

        public bool Enabled { get; private set; }

        public int IntervalHours { get; private set; }

        /// <summary>Scheduled snapshots older than this many days are removed - but never below <see cref="KeepLast"/>.</summary>
        public int RetentionDays { get; private set; }

        public int KeepLast { get; private set; }

        public DateTime CreatedOnUtc { get; private set; }

        public DateTime? LastRunOnUtc { get; private set; }

        public static BackupScheduleAggregate Create(int intervalHours, int retentionDays, int keepLast, bool enabled, DateTime nowUtc)
        {
            var schedule = new BackupScheduleAggregate { Id = Guid.NewGuid(), CreatedOnUtc = Utc(nowUtc) };
            schedule.Redefine(intervalHours, retentionDays, keepLast, enabled);
            return schedule;
        }

        public void Redefine(int intervalHours, int retentionDays, int keepLast, bool enabled)
        {
            if (intervalHours < 1 || intervalHours > MaximumIntervalHours)
            {
                throw new ArgumentOutOfRangeException(nameof(intervalHours), $"A schedule runs every 1-{MaximumIntervalHours} hours.");
            }

            if (retentionDays < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(retentionDays), "Snapshots are kept at least a day.");
            }

            if (keepLast < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(keepLast), "At least the latest snapshot is kept.");
            }

            IntervalHours = intervalHours;
            RetentionDays = retentionDays;
            KeepLast = keepLast;
            Enabled = enabled;
        }

        /// <summary>Due when enabled and never run, or when a full interval has passed since the last run.</summary>
        public bool IsDue(DateTime nowUtc) => Enabled && (LastRunOnUtc is not { } last || Utc(nowUtc) >= last.AddHours(IntervalHours));

        public void MarkRun(DateTime nowUtc) => LastRunOnUtc = Utc(nowUtc);

        private static DateTime Utc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => throw new ArgumentException("An instant is UTC.", nameof(value)),
        };
    }
}
