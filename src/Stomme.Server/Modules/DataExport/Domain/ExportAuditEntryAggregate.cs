#nullable enable
using System;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.DataExport.Domain
{
    /// <summary>
    /// One line of the export audit trail: who did what to which export, and when.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Written by the application code at each transition, never by a domain-event handler.</b> the predecessor platform's
    /// audit handler called <c>SaveChangesAsync</c> from inside a domain-event handler, which D40 forbids - a
    /// handler stages work and never manages the unit of work. So nothing here raises an event; the command or
    /// the job that moves a job also adds its entry.
    /// </para>
    /// <para>
    /// Immutable once written: there is no mutator. An entry outlives its job's artifact and is removed only by
    /// the retention purge.
    /// </para>
    /// </remarks>
    public sealed class ExportAuditEntryAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        private DateTime _occurredOnUtc;

        // Materialisation constructor. Not for application use.
        private ExportAuditEntryAggregate()
        {
            TenantId = null!;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public Guid JobId { get; private set; }

        /// <summary>The acting user; null when the system acted (the queue job, the expiry pass).</summary>
        public int? ActorUserId { get; private set; }

        public ExportAuditAction Action { get; private set; }

        public ExportFailureCode? FailureCode { get; private set; }

        public DateTime OccurredOnUtc
        {
            get => ExportInstant.Stamp(_occurredOnUtc);
            private set => _occurredOnUtc = value;
        }

        public static ExportAuditEntryAggregate Record(
            Guid id,
            TenantId tenantId,
            int siteId,
            Guid jobId,
            int? actorUserId,
            ExportAuditAction action,
            DateTime nowUtc,
            ExportFailureCode? failureCode = null)
        {
            ArgumentNullException.ThrowIfNull(tenantId);

            if (id == Guid.Empty)
            {
                throw new ArgumentException("An audit entry needs an id.", nameof(id));
            }

            if (jobId == Guid.Empty)
            {
                throw new ArgumentException("An audit entry names its export.", nameof(jobId));
            }

            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "An audit entry belongs to a site.");
            }

            if (actorUserId is <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(actorUserId), "An actor is a real user, or null for the system.");
            }

            if (!Enum.IsDefined(action))
            {
                throw new ArgumentOutOfRangeException(nameof(action));
            }

            if (failureCode is { } code && !Enum.IsDefined(code))
            {
                throw new ArgumentOutOfRangeException(nameof(failureCode));
            }

            if ((action == ExportAuditAction.Failed) != failureCode.HasValue)
            {
                throw new ArgumentException("A failure code accompanies a Failed entry, and only that.", nameof(failureCode));
            }

            return new ExportAuditEntryAggregate
            {
                Id = id,
                TenantId = tenantId,
                SiteId = siteId,
                JobId = jobId,
                ActorUserId = actorUserId,
                Action = action,
                FailureCode = failureCode,
                OccurredOnUtc = ExportInstant.Require(nowUtc, nameof(nowUtc)),
            };
        }
    }
}
