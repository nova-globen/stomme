#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Extensions;
using Stomme.Managers;
using Stomme.Models.Sessions;
using Stomme.Modules.Sessions.Application;
using Stomme.Modules.Sessions.Domain;
using Stomme.Security;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;

namespace Stomme.Modules.Sessions.Services
{
    public enum RevokeOutcome
    {
        NotFound,
        Revoked,
        Forbidden,
    }

    /// <summary>
    /// The SERVER side of <see cref="ISessionService"/> (BL-119), and what the controller calls: every decision - who the
    /// caller is, which site, whether they are an Administrator, whether the session is theirs - is made here from the
    /// request, never from an argument.
    /// </summary>
    public sealed class ServerSessionService(ISessionRegistry registry, IUserManager users, SiteState siteState, IHttpContextAccessor accessor) : ISessionService
    {
        public const int MaximumTake = 2000;

        public async Task<List<UserSessionView>> GetMineAsync()
        {
            var (alias, principal) = Caller();
            var userId = principal?.UserId() ?? -1;
            if (alias is null || userId <= 0)
            {
                return new List<UserSessionView>();
            }

            var username = principal!.Username();
            return (await registry.ListForUserAsync(alias, userId)).Select(session => View(session, username, principal)).ToList();
        }

        public async Task<bool> RevokeAsync(Guid sessionId) => await RevokeMineAsync(sessionId);

        public async Task<bool> RevokeMineAsync(Guid sessionId)
        {
            var (alias, principal) = Caller();
            var userId = principal?.UserId() ?? -1;
            return alias is not null && userId > 0 && await registry.RevokeAsync(alias, sessionId, userId, "revoked by its owner", ownerUserId: userId);
        }

        public async Task<List<UserSessionView>> GetSiteAsync(int take)
        {
            var (alias, principal) = Caller();
            if (alias is null || principal?.IsInRole(RoleNames.Admin) != true)
            {
                return new List<UserSessionView>();
            }

            var rows = await registry.ListForSiteAsync(alias, Math.Clamp(take <= 0 ? 500 : take, 1, MaximumTake));
            var names = new Dictionary<int, string>();
            foreach (var userId in rows.Select(row => row.UserId).Distinct())
            {
                names[userId] = (await users.GetUserAsync(userId, alias.SiteId))?.Username ?? string.Empty;
            }

            return rows.Select(row => View(row, names[row.UserId], principal)).ToList();
        }

        public async Task<bool> RevokeSiteSessionAsync(Guid sessionId) => await RevokeAnyAsync(sessionId) == RevokeOutcome.Revoked;

        /// <summary>
        /// An Administrator ends any session of this site - except a Host's, unless they are a Host themselves: a site
        /// administrator must not be able to sign the installation's host out.
        /// </summary>
        public async Task<RevokeOutcome> RevokeAnyAsync(Guid sessionId)
        {
            var (alias, principal) = Caller();
            if (alias is null || principal?.IsInRole(RoleNames.Admin) != true)
            {
                return RevokeOutcome.Forbidden;
            }

            var target = await registry.FindActiveAsync(alias, sessionId);
            if (target is null)
            {
                return RevokeOutcome.NotFound;
            }

            if (!principal.IsInRole(RoleNames.Host) && UserSecurity.ContainsRole((await users.GetUserAsync(target.UserId, alias.SiteId))?.Roles ?? string.Empty, RoleNames.Host))
            {
                return RevokeOutcome.Forbidden;
            }

            return await registry.RevokeAsync(alias, sessionId, principal.UserId(), "revoked by an administrator")
                ? RevokeOutcome.Revoked
                : RevokeOutcome.NotFound;
        }

        private (Models.Alias? Alias, System.Security.Claims.ClaimsPrincipal? Principal) Caller()
        {
            var principal = accessor.HttpContext?.User;
            var alias = siteState.Alias;
            return (alias is { SiteId: > 0 } ? alias : null, principal?.Identity?.IsAuthenticated == true ? principal : null);
        }

        private static UserSessionView View(UserSessionAggregate session, string username, System.Security.Claims.ClaimsPrincipal? caller) =>
            new(session.Id, session.UserId, username, session.CreatedOnUtc, session.LastSeenOnUtc, session.ExpiresOnUtc,
                session.RemoteAddress, session.UserAgent, caller?.SessionId() == session.Id);
    }
}
