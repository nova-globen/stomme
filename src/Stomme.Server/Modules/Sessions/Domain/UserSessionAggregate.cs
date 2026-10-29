#nullable enable
using System;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Modules.Sessions.Domain
{
    /// <summary>
    /// One server-side session: the row an authentication cookie points at, so that ONE sign-in can be listed and
    /// revoked, and a cookie whose row is revoked or gone is refused on its next request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a row.</b> Stock Identity can only change the security stamp, which ends EVERY session of the account at
    /// once and reaches a cookie on its next validation. A stolen cookie could therefore not be ended on its own, and
    /// nothing could list where an account is signed in.
    /// </para>
    /// <para>
    /// <b>"Ended" is one field.</b> A revoke moves <see cref="ExpiresOnUtc"/> to the revocation instant, so a session is
    /// active exactly while it is not revoked and <see cref="ExpiresOnUtc"/> is in the future, and retention deletes by
    /// <see cref="ExpiresOnUtc"/> alone - one range filter, the safe set on every provider.
    /// </para>
    /// </remarks>
    public sealed class UserSessionAggregate : AggregateRoot<Guid>, ITenantOwned
    {
        public const int MaximumAddressLength = 64;
        public const int MaximumUserAgentLength = 256;
        public const int MaximumReasonLength = 64;

        /// <summary>How stale <see cref="LastSeenOnUtc"/> may get before a request writes it again - a write per session per five minutes, not per request.</summary>
        public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

        // Materialisation constructor. Not for application use.
        private UserSessionAggregate()
        {
            TenantId = null!;
            RemoteAddress = string.Empty;
            UserAgent = string.Empty;
            RevokeReason = string.Empty;
        }

        public TenantId TenantId { get; private set; }

        public int SiteId { get; private set; }

        public int UserId { get; private set; }

        public DateTime CreatedOnUtc { get; private set; }

        public DateTime LastSeenOnUtc { get; private set; }

        /// <summary>When the cookie stops being accepted - its own expiry, moved to the revocation instant by a revoke.</summary>
        public DateTime ExpiresOnUtc { get; private set; }

        public string RemoteAddress { get; private set; }

        public string UserAgent { get; private set; }

        public DateTime? RevokedOnUtc { get; private set; }

        /// <summary>Who revoked it: the session's own user, an administrator, or null for the framework (a sign-out, a replaced cookie).</summary>
        public int? RevokedByUserId { get; private set; }

        public string RevokeReason { get; private set; }

        public static UserSessionAggregate Open(
            TenantId tenantId, int siteId, int userId, DateTime nowUtc, DateTime expiresUtc, string? remoteAddress, string? userAgent)
        {
            ArgumentNullException.ThrowIfNull(tenantId);
            if (siteId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(siteId), "A session belongs to a site.");
            }

            if (userId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(userId), "A session belongs to a user.");
            }

            var now = Utc(nowUtc);
            var expires = Utc(expiresUtc);
            return new UserSessionAggregate
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                SiteId = siteId,
                UserId = userId,
                CreatedOnUtc = now,
                LastSeenOnUtc = now,
                ExpiresOnUtc = expires > now ? expires : now,
                RemoteAddress = Clip(remoteAddress, MaximumAddressLength),
                UserAgent = Clip(userAgent, MaximumUserAgentLength),
            };
        }

        public bool IsActiveAt(DateTime nowUtc) => RevokedOnUtc is null && ExpiresOnUtc > Utc(nowUtc);

        /// <summary>Records that the session was used. False - and nothing changed - when the last record is recent enough.</summary>
        public bool Touch(DateTime nowUtc, DateTime? expiresUtc)
        {
            var now = Utc(nowUtc);
            if (!IsActiveAt(now) || now - LastSeenOnUtc < TouchInterval)
            {
                return false;
            }

            LastSeenOnUtc = now;
            if (expiresUtc is { } expires && Utc(expires) > now)
            {
                ExpiresOnUtc = Utc(expires); // a sliding cookie moved its own expiry forward
            }

            return true;
        }

        /// <summary>Ends the session. False when it had already ended - a revoke is idempotent and keeps the FIRST reason.</summary>
        public bool Revoke(DateTime nowUtc, int? byUserId, string reason)
        {
            var now = Utc(nowUtc);
            if (RevokedOnUtc is not null)
            {
                return false;
            }

            RevokedOnUtc = now;
            RevokedByUserId = byUserId is > 0 ? byUserId : null;
            RevokeReason = Clip(reason, MaximumReasonLength);
            if (ExpiresOnUtc > now)
            {
                ExpiresOnUtc = now;
            }

            return true;
        }

        private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

        private static string Clip(string? value, int max) =>
            string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];
    }
}
