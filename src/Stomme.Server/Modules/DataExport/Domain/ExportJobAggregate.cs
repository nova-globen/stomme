#nullable enable
using System;
using Stomme.Export;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.DataExport.Domain
{
    /// <summary>
    /// One requested export, from the moment it is queued to the moment its artifact expires (BL-85, D108).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The lease is a state transition, not a lock.</b> The host registers no <c>IDistributedLock</c> on any
    /// provider (D108 refutation 2), so what stops two drains rendering one job is <see cref="Claim"/> moving
    /// Queued to Rendering and the store's concurrency check rejecting the second writer. A claim carries a lease
    /// expiry; a render that is still alive renews it with <see cref="Heartbeat"/>, and only a lease that ran out
    /// with no heartbeat may be taken over by <see cref="Reclaim"/> (refutation 9) - a fixed timeout alone would
    /// reclaim a healthy long export while it was still writing.
    /// </para>
    /// <para>
    /// <b>Scalars only, and computed members are METHODS.</b> Both document providers serialise get-only
    /// properties, so a computed property would become a stored field on RavenDB and File with no EF column - a
    /// provider divergence invisible in the code. <see cref="ContentType"/> and <see cref="FileExtension"/> are
    /// methods for that reason.
    /// </para>
    /// <para>
    /// <b>Every instant is UTC and re-stamped on read</b> (refutation 13): a relational provider hands a DateTime
    /// back with no kind, and <c>DateTime</c> equality ignores the kind, so an unstamped value passes every
    /// round-trip assertion and then shifts by the host offset the first time it is converted.
    /// </para>
    /// </remarks>
    public sealed class ExportJobAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaximumArgumentsLength = 4000;

        public const int MaximumOptionsLength = 1000;

        /// <summary>How many times a job may be claimed before it is failed instead of re-queued.</summary>
        public const int MaximumAttempts = 3;

        private DateTime _requestedOnUtc;
        private DateTime? _startedOnUtc;
        private DateTime? _completedOnUtc;
        private DateTime? _expiresOnUtc;
        private DateTime? _leaseExpiresOnUtc;
        private DateTime? _heartbeatOnUtc;

        // Materialisation constructor. Not for application use.
        private ExportJobAggregate()
        {
            TenantId = null!;
            SourceKey = string.Empty;
            OptionsJson = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        /// <summary>The user the export runs AS: re-authorised from stored rows when the job is claimed.</summary>
        public int RequesterUserId { get; private set; }

        /// <summary>Canonical (<see cref="ExportSourceKeys.IsCanonical"/>), so it is its own normalised column.</summary>
        public string SourceKey { get; private set; }

        public string? ArgumentsJson { get; private set; }

        public string OptionsJson { get; private set; }

        public ExportFormat Format { get; private set; }

        public ExportJobStatus Status { get; private set; }

        public DateTime RequestedOnUtc
        {
            get => ExportInstant.Stamp(_requestedOnUtc);
            private set => _requestedOnUtc = value;
        }

        public DateTime? StartedOnUtc
        {
            get => ExportInstant.Stamp(_startedOnUtc);
            private set => _startedOnUtc = value;
        }

        public DateTime? CompletedOnUtc
        {
            get => ExportInstant.Stamp(_completedOnUtc);
            private set => _completedOnUtc = value;
        }

        /// <summary>When a Ready artifact stops being downloadable and becomes purgeable.</summary>
        public DateTime? ExpiresOnUtc
        {
            get => ExportInstant.Stamp(_expiresOnUtc);
            private set => _expiresOnUtc = value;
        }

        public DateTime? LeaseExpiresOnUtc
        {
            get => ExportInstant.Stamp(_leaseExpiresOnUtc);
            private set => _leaseExpiresOnUtc = value;
        }

        public DateTime? HeartbeatOnUtc
        {
            get => ExportInstant.Stamp(_heartbeatOnUtc);
            private set => _heartbeatOnUtc = value;
        }

        /// <summary>How many times the job has been claimed.</summary>
        public int Attempts { get; private set; }

        public long? RowCount { get; private set; }

        public long? SizeBytes { get; private set; }

        /// <summary>The framework File row holding the artifact, once Ready.</summary>
        public int? FileId { get; private set; }

        /// <summary>A CODE, never text: the exception message goes to the log, not to the requester.</summary>
        public ExportFailureCode? FailureCode { get; private set; }

        /// <summary>The schedule whose run queued this export (BL-95); null for one a person requested.</summary>
        public Guid? ScheduleId { get; private set; }

        public static ExportJobAggregate Request(
            Guid id,
            TenantId tenantId,
            int siteId,
            int requesterUserId,
            string sourceKey,
            ExportFormat format,
            string? argumentsJson,
            string optionsJson,
            DateTime nowUtc,
            Guid? scheduleId = null)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            ArgumentNullException.ThrowIfNull(optionsJson);

            if (id == Guid.Empty)
            {
                throw new ArgumentException("An export job needs an id.", nameof(id));
            }

            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "An export belongs to a site.");
            }

            if (requesterUserId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(requesterUserId), "An export is requested by a user.");
            }

            if (!ExportSourceKeys.IsCanonical(sourceKey))
            {
                // Rejected, never rewritten: two spellings must not name one source (see ExportSourceKeys).
                throw new ArgumentException("The source key is not canonical.", nameof(sourceKey));
            }

            if (!Enum.IsDefined(format))
            {
                throw new ArgumentOutOfRangeException(nameof(format));
            }

            if (argumentsJson is { Length: > MaximumArgumentsLength })
            {
                throw new ArgumentException($"Arguments are at most {MaximumArgumentsLength} characters.", nameof(argumentsJson));
            }

            if (optionsJson.Length > MaximumOptionsLength)
            {
                throw new ArgumentException($"Options are at most {MaximumOptionsLength} characters.", nameof(optionsJson));
            }

            return new ExportJobAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                RequesterUserId = requesterUserId,
                SourceKey = sourceKey,
                Format = format,
                ArgumentsJson = argumentsJson,
                OptionsJson = optionsJson,
                Status = ExportJobStatus.Queued,
                RequestedOnUtc = ExportInstant.Require(nowUtc, nameof(nowUtc)),
                ScheduleId = scheduleId,
            };
        }

        /// <summary>Queued to Rendering. The store's concurrency check is what makes this exactly-once.</summary>
        public void Claim(DateTime nowUtc, TimeSpan lease)
        {
            if (Status != ExportJobStatus.Queued)
            {
                throw new InvalidOperationException($"Only a Queued export can be claimed; this one is {Status}.");
            }

            BeginAttempt(nowUtc, lease);
        }

        /// <summary>Whether a Rendering job's lease ran out with no heartbeat to renew it.</summary>
        public bool IsLeaseExpired(DateTime nowUtc) =>
            Status == ExportJobStatus.Rendering
            && LeaseExpiresOnUtc is { } expiry
            && expiry <= ExportInstant.Require(nowUtc, nameof(nowUtc));

        /// <summary>
        /// Takes over a Rendering job whose lease ran out. Returns false - and FAILS the job - when the attempts
        /// are spent, so an export that kills its renderer every time does not loop for ever.
        /// </summary>
        public bool Reclaim(DateTime nowUtc, TimeSpan lease)
        {
            if (!IsLeaseExpired(nowUtc))
            {
                throw new InvalidOperationException("Only a Rendering export whose lease has expired can be reclaimed.");
            }

            if (Attempts >= MaximumAttempts)
            {
                Fail(ExportFailureCode.AttemptsExhausted, nowUtc);
                return false;
            }

            BeginAttempt(nowUtc, lease);
            return true;
        }

        /// <summary>Renews the lease. Called between tables and every N rows by the render that holds it.</summary>
        public void Heartbeat(DateTime nowUtc, TimeSpan lease)
        {
            if (Status != ExportJobStatus.Rendering)
            {
                throw new InvalidOperationException($"Only a Rendering export has a lease to renew; this one is {Status}.");
            }

            RequirePositive(lease);
            var now = ExportInstant.Require(nowUtc, nameof(nowUtc));
            HeartbeatOnUtc = now;
            LeaseExpiresOnUtc = now + lease;
        }

        public void Complete(int fileId, long rowCount, long sizeBytes, DateTime nowUtc, TimeSpan retention)
        {
            if (Status != ExportJobStatus.Rendering)
            {
                throw new InvalidOperationException($"Only a Rendering export can complete; this one is {Status}.");
            }

            if (fileId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(fileId));
            }

            if (rowCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(rowCount));
            }

            if (sizeBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeBytes));
            }

            RequirePositive(retention);
            var now = ExportInstant.Require(nowUtc, nameof(nowUtc));

            Status = ExportJobStatus.Ready;
            FileId = fileId;
            RowCount = rowCount;
            SizeBytes = sizeBytes;
            CompletedOnUtc = now;
            ExpiresOnUtc = now + retention;
            LeaseExpiresOnUtc = null;
        }

        public void Fail(ExportFailureCode code, DateTime nowUtc)
        {
            if (Status is not (ExportJobStatus.Queued or ExportJobStatus.Rendering))
            {
                throw new InvalidOperationException($"A {Status} export cannot fail.");
            }

            if (!Enum.IsDefined(code))
            {
                throw new ArgumentOutOfRangeException(nameof(code));
            }

            Status = ExportJobStatus.Failed;
            FailureCode = code;
            CompletedOnUtc = ExportInstant.Require(nowUtc, nameof(nowUtc));
            LeaseExpiresOnUtc = null;
        }

        /// <summary>
        /// Cancels work that has not finished. A render in flight notices on its next status re-read.
        /// </summary>
        public void Cancel(DateTime nowUtc)
        {
            if (Status is not (ExportJobStatus.Queued or ExportJobStatus.Rendering))
            {
                throw new InvalidOperationException($"A {Status} export cannot be cancelled.");
            }

            Status = ExportJobStatus.Cancelled;
            CompletedOnUtc = ExportInstant.Require(nowUtc, nameof(nowUtc));
            LeaseExpiresOnUtc = null;
        }

        /// <summary>Whether a Ready artifact has outlived its retention.</summary>
        public bool IsDue(DateTime nowUtc) =>
            Status == ExportJobStatus.Ready
            && ExpiresOnUtc is { } expiry
            && expiry <= ExportInstant.Require(nowUtc, nameof(nowUtc));

        /// <summary>Ready to Expired: the artifact is gone, the row stays as history.</summary>
        public void Expire(DateTime nowUtc)
        {
            if (Status != ExportJobStatus.Ready)
            {
                throw new InvalidOperationException($"Only a Ready export expires; this one is {Status}.");
            }

            Status = ExportJobStatus.Expired;
            FileId = null;
            CompletedOnUtc = ExportInstant.Require(nowUtc, nameof(nowUtc));
        }

        /// <summary>Whether the artifact may be handed out right now.</summary>
        public bool IsDownloadable(DateTime nowUtc) =>
            Status == ExportJobStatus.Ready && FileId is not null && !IsDue(nowUtc);

        /// <summary>A method, not a property - see the type remarks.</summary>
        public string ContentType() => Format switch
        {
            ExportFormat.Csv => "text/csv",
            ExportFormat.Xlsx => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ExportFormat.Json => "application/json",
            ExportFormat.Pdf => "application/pdf",
            _ => "application/octet-stream",
        };

        /// <summary>A method, not a property - see the type remarks. Includes the dot.</summary>
        public string FileExtension() => Format switch
        {
            ExportFormat.Csv => ".csv",
            ExportFormat.Xlsx => ".xlsx",
            ExportFormat.Json => ".json",
            ExportFormat.Pdf => ".pdf",
            _ => ".bin",
        };

        private void BeginAttempt(DateTime nowUtc, TimeSpan lease)
        {
            RequirePositive(lease);
            var now = ExportInstant.Require(nowUtc, nameof(nowUtc));

            Status = ExportJobStatus.Rendering;
            Attempts++;
            StartedOnUtc = now;
            HeartbeatOnUtc = now;
            LeaseExpiresOnUtc = now + lease;
        }

        private static void RequirePositive(TimeSpan span)
        {
            if (span <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(span), "A duration is positive.");
            }
        }
    }
}
