#nullable enable
using System;
using Cronos;
using Stomme.Export;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.DataExport.Domain
{
    /// <summary>Why a schedule stopped running. Stored as its integer value; never renumber.</summary>
    public enum ExportSchedulePauseReason
    {
        None = 0,

        /// <summary>Its owner or an administrator paused it.</summary>
        Manual = 1,

        /// <summary>A run found the owner no longer allowed to make this export (D108's correction: never run without them).</summary>
        OwnerNotAllowed = 2,

        /// <summary>A run found the source gone from the host.</summary>
        SourceUnavailable = 3,
    }

    /// <summary>
    /// A recurring export (D108's correction of 2026-10-05): each occurrence of its cron queues an ORDINARY export job
    /// whose requester is the schedule's OWNER, re-authorised from stored rows at every run - so a schedule never carries a
    /// permission of its own, and a run the owner may no longer make PAUSES it instead of running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>UTC cron, five fields</b> (minute hour day-of-month month day-of-week), parsed by Cronos - the library the
    /// pipelines' schedule trigger uses. Occurrences must be at least <see cref="MinimumInterval"/> apart: an every-minute
    /// export would fill the site's storage and the queue with no one reading it.
    /// </para>
    /// <para>
    /// <b>Missed occurrences run once.</b> A host that was down past several occurrences queues ONE job when it next looks,
    /// then the next occurrence after now - the same rule as the pipelines' cron trigger.
    /// </para>
    /// <para>
    /// <b>The claim is the update.</b> <see cref="RecordRun"/> moves <see cref="NextRunOnUtc"/> forward and the store's
    /// concurrency check rejects a second host making the same move, which is what keeps one occurrence to one job - the
    /// job is queued only after the schedule is saved.
    /// </para>
    /// <para>Scalars only and UTC re-stamped on read, as <see cref="ExportJobAggregate"/> explains.</para>
    /// </remarks>
    public sealed class ExportScheduleAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaximumNameLength = 100;

        public const int MaximumCronLength = 100;

        public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(15);

        private DateTime _createdOnUtc;
        private DateTime? _nextRunOnUtc;
        private DateTime? _lastRunOnUtc;
        private DateTime? _pausedOnUtc;

        // Materialisation constructor. Not for application use.
        private ExportScheduleAggregate()
        {
            TenantId = null!;
            Name = string.Empty;
            SourceKey = string.Empty;
            OptionsJson = string.Empty;
            Cron = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        /// <summary>The user every run is requested AS, re-authorised at each run.</summary>
        public int OwnerUserId { get; private set; }

        public string Name { get; private set; }

        public string SourceKey { get; private set; }

        public ExportFormat Format { get; private set; }

        public string? ArgumentsJson { get; private set; }

        public string OptionsJson { get; private set; }

        public string Cron { get; private set; }

        public bool IsPaused { get; private set; }

        public ExportSchedulePauseReason PauseReason { get; private set; }

        /// <summary>The export the last run queued.</summary>
        public Guid? LastJobId { get; private set; }

        public DateTime CreatedOnUtc
        {
            get => ExportInstant.Stamp(_createdOnUtc);
            private set => _createdOnUtc = value;
        }

        /// <summary>The next occurrence; null while paused.</summary>
        public DateTime? NextRunOnUtc
        {
            get => ExportInstant.Stamp(_nextRunOnUtc);
            private set => _nextRunOnUtc = value;
        }

        public DateTime? LastRunOnUtc
        {
            get => ExportInstant.Stamp(_lastRunOnUtc);
            private set => _lastRunOnUtc = value;
        }

        public DateTime? PausedOnUtc
        {
            get => ExportInstant.Stamp(_pausedOnUtc);
            private set => _pausedOnUtc = value;
        }

        /// <summary>A refusal, or null when <paramref name="cron"/> is a five-field UTC cron whose occurrences are far enough apart.</summary>
        public static string? CronProblem(string? cron)
        {
            if (string.IsNullOrWhiteSpace(cron) || cron.Length > MaximumCronLength)
            {
                return "The schedule needs a cron expression of five fields (minute hour day-of-month month day-of-week), in UTC.";
            }

            if (!CronExpression.TryParse(cron.Trim(), CronFormat.Standard, out var expression))
            {
                return "That is not a five-field cron expression (minute hour day-of-month month day-of-week), in UTC.";
            }

            // Ten consecutive gaps from a fixed instant: enough to catch "*/5" and "0-59/1" without enumerating a year.
            var from = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
            var previous = expression.GetNextOccurrence(from, TimeZoneInfo.Utc);
            if (previous is null)
            {
                return "That cron expression never occurs.";
            }

            for (var index = 0; index < 10; index++)
            {
                var next = expression.GetNextOccurrence(previous.Value, TimeZoneInfo.Utc);
                if (next is null)
                {
                    break;
                }

                if (next.Value - previous.Value < MinimumInterval)
                {
                    return $"Runs must be at least {MinimumInterval.TotalMinutes:0} minutes apart.";
                }

                previous = next;
            }

            return null;
        }

        public static ExportScheduleAggregate Create(
            Guid id,
            TenantId tenantId,
            int siteId,
            int ownerUserId,
            string name,
            string sourceKey,
            ExportFormat format,
            string? argumentsJson,
            string optionsJson,
            string cron,
            DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            ArgumentNullException.ThrowIfNull(optionsJson);
            if (id == Guid.Empty)
            {
                throw new ArgumentException("A schedule needs an id.", nameof(id));
            }

            if (siteId <= 0 || ownerUserId <= 0)
            {
                throw new ArgumentOutOfRangeException(siteId <= 0 ? nameof(siteId) : nameof(ownerUserId));
            }

            if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaximumNameLength)
            {
                throw new ArgumentException($"A schedule needs a name of at most {MaximumNameLength} characters.", nameof(name));
            }

            if (!ExportSourceKeys.IsCanonical(sourceKey))
            {
                throw new ArgumentException("The source key is not canonical.", nameof(sourceKey));
            }

            if (!Enum.IsDefined(format))
            {
                throw new ArgumentOutOfRangeException(nameof(format));
            }

            if (argumentsJson is { Length: > ExportJobAggregate.MaximumArgumentsLength } || optionsJson.Length > ExportJobAggregate.MaximumOptionsLength)
            {
                throw new ArgumentException("The arguments or options are too long.");
            }

            if (CronProblem(cron) is { } problem)
            {
                throw new ArgumentException(problem, nameof(cron));
            }

            var now = ExportInstant.Require(nowUtc, nameof(nowUtc));
            var schedule = new ExportScheduleAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                OwnerUserId = ownerUserId,
                Name = name.Trim(),
                SourceKey = sourceKey,
                Format = format,
                ArgumentsJson = argumentsJson,
                OptionsJson = optionsJson,
                Cron = cron.Trim(),
                CreatedOnUtc = now,
            };
            schedule.NextRunOnUtc = schedule.NextAfter(now);
            return schedule;
        }

        public bool IsDue(DateTime nowUtc) => !IsPaused && NextRunOnUtc is { } next && next <= ExportInstant.Require(nowUtc, nameof(nowUtc));

        /// <summary>Claims the due occurrence: every missed one collapses into this run, and the next is after now.</summary>
        public void RecordRun(Guid jobId, DateTime nowUtc)
        {
            if (!IsDue(nowUtc))
            {
                throw new InvalidOperationException("Only a due schedule runs.");
            }

            var now = ExportInstant.Require(nowUtc, nameof(nowUtc));
            LastRunOnUtc = now;
            LastJobId = jobId;
            NextRunOnUtc = NextAfter(now);
        }

        public void Pause(ExportSchedulePauseReason reason, DateTime nowUtc)
        {
            if (reason == ExportSchedulePauseReason.None || !Enum.IsDefined(reason))
            {
                throw new ArgumentOutOfRangeException(nameof(reason));
            }

            IsPaused = true;
            PauseReason = reason;
            PausedOnUtc = ExportInstant.Require(nowUtc, nameof(nowUtc));
            NextRunOnUtc = null;
        }

        /// <summary>Runs again from the next occurrence after now - a paused schedule never catches up.</summary>
        public void Resume(DateTime nowUtc)
        {
            var now = ExportInstant.Require(nowUtc, nameof(nowUtc));
            IsPaused = false;
            PauseReason = ExportSchedulePauseReason.None;
            PausedOnUtc = null;
            NextRunOnUtc = NextAfter(now);
        }

        /// <summary>An administrator takes the schedule over: every later run is requested as them.</summary>
        public void TakeOver(int newOwnerUserId)
        {
            if (newOwnerUserId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(newOwnerUserId));
            }

            OwnerUserId = newOwnerUserId;
        }

        private DateTime? NextAfter(DateTime nowUtc) =>
            CronExpression.Parse(Cron, CronFormat.Standard).GetNextOccurrence(nowUtc, TimeZoneInfo.Utc);
    }
}
