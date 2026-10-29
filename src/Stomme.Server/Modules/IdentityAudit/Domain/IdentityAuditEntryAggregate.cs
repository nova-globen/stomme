#nullable enable
using System;
using Stomme.Models.IdentityAudit;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.IdentityAudit.Domain
{
    /// <summary>
    /// One line of the identity audit ledger: a sign-in, a lockout, a password reset, a role change - who, to
    /// whom, on which site, when, from where.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A durable row, not a log line.</b> These events were only <c>LogFunction.Security</c> rows, and the log is not
    /// an audit: <c>LogManager</c> drops entries below <c>Logging:LogLevel:Default</c>, diverts to a file under load, and
    /// <c>PurgeJob</c> ages the table out on a setting any administrator can lower (the same reasons that gave the MCP module
    /// its own ledger).
    /// </para>
    /// <para>
    /// <b>What is NOT kept:</b> a password, a token, and - for a sign-in attempt on a user name that does not exist - the
    /// name typed, because people type their password into the user-name field. The row then says only that an unknown
    /// account was tried, and from where.
    /// </para>
    /// <para>Immutable and tenant-owned. Removed only by its retention pass, which has a floor.</para>
    /// </remarks>
    public sealed class IdentityAuditEntryAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaximumNameLength = 256;
        public const int MaximumAddressLength = 64;
        public const int MaximumDetailLength = 256;

        // Materialisation constructor. Not for application use.
        private IdentityAuditEntryAggregate()
        {
            TenantId = null!;
            SubjectName = string.Empty;
            RemoteAddress = string.Empty;
            Detail = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public DateTime OccurredOnUtc { get; private set; }

        public IdentityAuditEvent Event { get; private set; }

        /// <summary>Who did it - the signed-in user; null for an anonymous request such as a sign-in attempt.</summary>
        public int? ActorUserId { get; private set; }

        /// <summary>Whose account it happened to, when the account is known.</summary>
        public int? SubjectUserId { get; private set; }

        public string SubjectName { get; private set; }

        public string RemoteAddress { get; private set; }

        /// <summary>A short, secret-free detail: the role granted, the external provider linked, why a sign-in was denied.</summary>
        public string Detail { get; private set; }

        public static IdentityAuditEntryAggregate Record(
            TenantId tenantId, int siteId, IdentityAuditEvent evt, int? actorUserId, int? subjectUserId,
            string? subjectName, string? remoteAddress, string? detail, DateTime nowUtc)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "An identity audit entry belongs to a site.");
            }

            if (!Enum.IsDefined(evt))
            {
                throw new ArgumentOutOfRangeException(nameof(evt), "Not an identity audit event.");
            }

            return new IdentityAuditEntryAggregate
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SiteId = siteId,
                OccurredOnUtc = nowUtc.Kind == DateTimeKind.Utc ? nowUtc : nowUtc.ToUniversalTime(),
                Event = evt,
                ActorUserId = actorUserId is > 0 ? actorUserId : null,
                SubjectUserId = subjectUserId is > 0 ? subjectUserId : null,
                SubjectName = Clip(subjectName, MaximumNameLength),
                RemoteAddress = Clip(remoteAddress, MaximumAddressLength),
                Detail = Clip(detail, MaximumDetailLength),
            };
        }

        public IdentityAuditEntryView ToView() =>
            new(Id, OccurredOnUtc, Event, ActorUserId, SubjectUserId, SubjectName, RemoteAddress, Detail);

        private static string Clip(string? value, int max) =>
            string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
    }
}
