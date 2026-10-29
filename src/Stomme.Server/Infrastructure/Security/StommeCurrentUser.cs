#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Claims;
using Stomme.Extensions;
using Stomme.Security.Abstractions;
using Microsoft.AspNetCore.Http;

namespace Stomme.Infrastructure.Security
{
    /// <summary>
    /// <see cref="ICurrentUser"/> over the current request's principal: the user id, name and
    /// role claims Stomme's own <c>ClaimsPrincipalExtensions</c> read, plus the site the request resolved
    /// to. ONE registration, always; anonymous — and site -1 — when there is no <c>HttpContext</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately carries NO permission answer: the decision is asynchronous and per request, so it lives
    /// on <see cref="IAccessGuard"/> only (see the contract's remarks). The site is the request-resolved alias
    /// and NOTHING else — the identical expression <see cref="PermissionRowAccessGuard"/> scopes its rows by
    /// (<c>GetAlias()?.SiteId ?? -1</c>), so "who am I" and "what may I do" can never answer for two different
    /// sites on one request. An earlier draft fell back to the cookie's SiteKey claim here: on a request with
    /// no resolved alias that reported the cookie's site while the guard scoped to -1 (driven — the fallback
    /// answered 7 where the guard answered -1). Pinned by <c>StommeCurrentUserTests</c>.
    /// </remarks>
    public sealed class StommeCurrentUser : ICurrentUser
    {
        private readonly IHttpContextAccessor _accessor;

        public StommeCurrentUser(IHttpContextAccessor accessor)
        {
            _accessor = accessor;
        }

        private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

        public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

        public int? UserId
        {
            get
            {
                if (!IsAuthenticated)
                {
                    return null;
                }

                var userId = Principal!.UserId();
                return userId == -1 ? null : userId;
            }
        }

        public string? UserName
        {
            get
            {
                if (!IsAuthenticated)
                {
                    return null;
                }

                var name = Principal!.Identity?.Name;
                return string.IsNullOrEmpty(name) ? Principal.Username() : name;
            }
        }

        public IReadOnlySet<string> Roles =>
            Principal is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(Principal.Roles(), StringComparer.OrdinalIgnoreCase);

        public int SiteId => _accessor.HttpContext?.GetAlias()?.SiteId ?? -1;
    }
}
