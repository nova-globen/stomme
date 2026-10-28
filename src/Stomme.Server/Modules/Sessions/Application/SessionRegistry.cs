#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Models.IdentityAudit;
using Stomme.Modules.IdentityAudit.Application;
using Stomme.Modules.Sessions.Domain;
using Stomme.Sessions.Persistence;
using Stomme.Tenancy.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Sessions.Application
{
    /// <summary>
    /// The server-side sessions of a site (BL-119): opened when a cookie is issued, checked on every request that
    /// validates one, listed for their owner and for an administrator, and revoked one at a time or all together.
    /// </summary>
    public interface ISessionRegistry
    {
        /// <summary>Opens a session for a cookie about to be issued and returns its id.</summary>
        /// <exception cref="InvalidOperationException">No tenant or site is resolved - no cookie should be issued then.</exception>
        Task<Guid> OpenAsync(Alias alias, int userId, DateTimeOffset? cookieExpiresUtc);

        /// <summary>
        /// Is this cookie's session still active for this user on this site? Cached for <see cref="SessionRegistry.CheckCacheDuration"/>,
        /// and records the use at most once per <see cref="UserSessionAggregate.TouchInterval"/>.
        /// </summary>
        Task<bool> IsActiveAsync(Alias alias, Guid sessionId, int userId, DateTimeOffset? cookieExpiresUtc);

        /// <summary>Ends one session. <paramref name="ownerUserId"/>, when given, must own it - how a user is kept to their own sessions.</summary>
        /// <returns>True when a session of this site was ended by this call.</returns>
        Task<bool> RevokeAsync(Alias alias, Guid sessionId, int? byUserId, string reason, int? ownerUserId = null);

        /// <summary>Ends every active session of a user on this site except <paramref name="exceptSessionId"/>. Returns how many.</summary>
        Task<int> RevokeAllAsync(Alias alias, int userId, int? byUserId, string reason, Guid? exceptSessionId = null);

        /// <summary>One ACTIVE session of this site, or null.</summary>
        Task<UserSessionAggregate?> FindActiveAsync(Alias alias, Guid sessionId);

        Task<IReadOnlyList<UserSessionAggregate>> ListForUserAsync(Alias alias, int userId);

        Task<IReadOnlyList<UserSessionAggregate>> ListForSiteAsync(Alias alias, int take);
    }

    /// <summary>The registry, over the storage-blind session rows.</summary>
    /// <remarks>
    /// <para>
    /// <b>How fast a revoke lands.</b> On the node that revokes: the next request - the check's cache entry is removed.
    /// On another node: at most <see cref="CheckCacheDuration"/> later, or at once where the cache runs with its Redis
    /// backplane, because the removal goes through the same FusionCache. An Interactive Server circuit that is already
    /// open is NOT re-checked by this: it carries the principal it was opened with (the D99 limit, unchanged) - the
    /// revoke reaches it on its next HTTP request or reconnect.
    /// </para>
    /// <para>
    /// <b>Fail closed.</b> A row that cannot be found, belongs to another site or user, or has ended answers "not active".
    /// A store that throws is the caller's to handle - the cookie validator refuses the request without deleting the
    /// cookie, so a store blip does not sign anyone out for good.
    /// </para>
    /// </remarks>
    public sealed class SessionRegistry(
        IUserSessionRepository sessions,
        ITenantContext tenantContext,
        ICacheManager cache,
        IHttpContextAccessor accessor,
        ILogger<SessionRegistry> logger,
        IIdentityAuditRecorder? audit = null) : ISessionRegistry
    {
        public static readonly TimeSpan CheckCacheDuration = TimeSpan.FromSeconds(30);

        public const string CacheKeyPrefix = "UserSession:";

        public async Task<Guid> OpenAsync(Alias alias, int userId, DateTimeOffset? cookieExpiresUtc)
        {
            var tenant = tenantContext.CurrentTenantId;
            if (tenant is null || alias is null || alias.SiteId <= 0)
            {
                throw new InvalidOperationException("A session cannot be opened: no tenant or site is resolved for this request.");
            }

            var now = DateTime.UtcNow;
            var context = accessor.HttpContext;
            var session = UserSessionAggregate.Open(
                tenant, alias.SiteId, userId, now,
                cookieExpiresUtc?.UtcDateTime ?? now.AddDays(14),
                context?.Connection.RemoteIpAddress?.ToString(),
                context?.Request.Headers.UserAgent.ToString());
            await sessions.AddAsync(session);
            return session.Id;
        }

        public async Task<bool> IsActiveAsync(Alias alias, Guid sessionId, int userId, DateTimeOffset? cookieExpiresUtc)
        {
            var snapshot = await cache.GetCacheAsync(alias, CacheKeyPrefix + sessionId, async ct =>
                SessionSnapshot.Of(await sessions.FindSessionAsync(sessionId, ct)), CheckCacheDuration, CheckCacheDuration);

            var now = DateTime.UtcNow;
            var tenant = tenantContext.CurrentTenantId;
            if (snapshot is null || !snapshot.Found || tenant is null || snapshot.TenantId != tenant.Value
                || snapshot.SiteId != alias.SiteId || snapshot.UserId != userId || snapshot.Revoked || snapshot.ExpiresOnUtc <= now)
            {
                return false;
            }

            if (now - snapshot.LastSeenOnUtc >= UserSessionAggregate.TouchInterval)
            {
                await TouchAsync(alias, sessionId, now, cookieExpiresUtc);
            }

            return true;
        }

        public async Task<bool> RevokeAsync(Alias alias, Guid sessionId, int? byUserId, string reason, int? ownerUserId = null)
        {
            var session = await sessions.FindSessionAsync(sessionId);
            if (session is null || session.SiteId != alias.SiteId || (ownerUserId is { } owner && session.UserId != owner))
            {
                return false;
            }

            if (!session.Revoke(DateTime.UtcNow, byUserId, reason))
            {
                return false;
            }

            await sessions.UpdateAsync(session);
            await cache.RemoveCacheAsync(alias, CacheKeyPrefix + sessionId);
            await AuditAsync(session, reason);
            return true;
        }

        public async Task<int> RevokeAllAsync(Alias alias, int userId, int? byUserId, string reason, Guid? exceptSessionId = null)
        {
            var now = DateTime.UtcNow;
            var revoked = 0;
            foreach (var session in await sessions.ListActiveForUserAsync(alias.SiteId, userId, now))
            {
                if (session.Id == exceptSessionId || !session.Revoke(now, byUserId, reason))
                {
                    continue;
                }

                // One update per DISTINCT row, each read by this same list and never loaded twice - and the storage-blind
                // registration does not track reads (D79), so no two instances of one key ever meet in a context.
                await sessions.UpdateAsync(session);
                await cache.RemoveCacheAsync(alias, CacheKeyPrefix + session.Id);
                revoked++;
            }

            if (revoked > 0 && audit is not null)
            {
                await audit.RecordAsync(IdentityAuditEvent.SessionRevoked, userId, null, $"{reason} ({revoked} sessions)", alias.SiteId);
            }

            return revoked;
        }

        public async Task<UserSessionAggregate?> FindActiveAsync(Alias alias, Guid sessionId)
        {
            var session = await sessions.FindSessionAsync(sessionId);
            return session is not null && session.SiteId == alias.SiteId && session.IsActiveAt(DateTime.UtcNow) ? session : null;
        }

        public async Task<IReadOnlyList<UserSessionAggregate>> ListForUserAsync(Alias alias, int userId) =>
            await sessions.ListActiveForUserAsync(alias.SiteId, userId, DateTime.UtcNow);

        public async Task<IReadOnlyList<UserSessionAggregate>> ListForSiteAsync(Alias alias, int take) =>
            await sessions.ListActiveForSiteAsync(alias.SiteId, DateTime.UtcNow, take);

        private async Task TouchAsync(Alias alias, Guid sessionId, DateTime now, DateTimeOffset? cookieExpiresUtc)
        {
            try
            {
                var session = await sessions.FindSessionAsync(sessionId);
                if (session is not null && session.Touch(now, cookieExpiresUtc?.UtcDateTime))
                {
                    await sessions.UpdateAsync(session);
                }

                await cache.RemoveCacheAsync(alias, CacheKeyPrefix + sessionId);
            }
            catch (Exception ex)
            {
                // Two requests of one session touching at once is expected; the loser's write is the one that fails. The
                // "last seen" instant is a courtesy, never a reason to refuse a request.
                logger.LogDebug(ex, "Session {SessionId}: the last-seen instant was not recorded.", sessionId);
            }
        }

        private async Task AuditAsync(UserSessionAggregate session, string reason)
        {
            if (audit is not null)
            {
                await audit.RecordAsync(IdentityAuditEvent.SessionRevoked, session.UserId, null, reason, session.SiteId);
            }
        }

        /// <summary>What the per-request check needs, small enough to cache - and to serialise into an L2 cache.</summary>
        public sealed class SessionSnapshot
        {
            public bool Found { get; set; }

            public Guid TenantId { get; set; }

            public int SiteId { get; set; }

            public int UserId { get; set; }

            public bool Revoked { get; set; }

            public DateTime ExpiresOnUtc { get; set; }

            public DateTime LastSeenOnUtc { get; set; }

            public static SessionSnapshot Of(UserSessionAggregate? session) => session is null
                ? new SessionSnapshot { Found = false }
                : new SessionSnapshot
                {
                    Found = true,
                    TenantId = session.TenantId.Value,
                    SiteId = session.SiteId,
                    UserId = session.UserId,
                    Revoked = session.RevokedOnUtc is not null,
                    ExpiresOnUtc = session.ExpiresOnUtc,
                    LastSeenOnUtc = session.LastSeenOnUtc,
                };
        }
    }
}
