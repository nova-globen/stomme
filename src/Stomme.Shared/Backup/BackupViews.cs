#nullable enable
using System;
using System.Collections.Generic;

namespace Stomme.Backup
{
    /// <summary>One snapshot as the Host sees it. No path, no key, no passphrase - ever.</summary>
    public sealed class BackupSnapshotView
    {
        public Guid Id { get; set; }

        public string Status { get; set; } = string.Empty;

        public string RetentionTag { get; set; } = string.Empty;

        public string FrameworkVersion { get; set; } = string.Empty;

        public long SizeBytes { get; set; }

        public DateTime CreatedOnUtc { get; set; }

        public DateTime? CompletedOnUtc { get; set; }

        public DateTime? StagedOnUtc { get; set; }

        public DateTime? LastRestoredOnUtc { get; set; }

        /// <summary>Why creation or the last restore failed - an exception type or a stated reason.</summary>
        public string FailureReason { get; set; } = string.Empty;

        public bool CanRestore { get; set; }

        public bool CanDelete { get; set; }
    }

    /// <summary>A backup schedule.</summary>
    public sealed class BackupScheduleView
    {
        public Guid Id { get; set; }

        public bool Enabled { get; set; }

        public int IntervalHours { get; set; } = 24;

        public int RetentionDays { get; set; } = 30;

        public int KeepLast { get; set; } = 7;

        public DateTime? LastRunOnUtc { get; set; }
    }

    /// <summary>The operator's passphrase for one operation. Sent in a body over HTTPS; never in a URL, never stored.</summary>
    public sealed class BackupPassphraseRequest
    {
        public string Passphrase { get; set; } = string.Empty;

        /// <summary>
        /// For a restore: the tenants to put back, or null/empty for the whole installation. A selection that
        /// would split a shared physical database is refused - a tenant that shares its database is restored with the
        /// tenants it shares it with, or not at all.
        /// </summary>
        public List<int> TenantIds { get; set; }
    }

    /// <summary>What an operation did: accepted (queued or done), or refused with the reasons.</summary>
    public sealed class BackupOperationResult
    {
        public bool Accepted { get; set; }

        public Guid? SnapshotId { get; set; }

        public List<string> Problems { get; set; } = new();

        /// <summary>Whether the result is final - a queued restore's report is not, until the worker has run it.</summary>
        public bool Completed { get; set; }
    }
}
