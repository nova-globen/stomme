using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Stomme.Extensions;
using Stomme.Infrastructure;
using Stomme.Modules.Sessions.Application;
using Stomme.Shared;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Security
{
    /// <summary>
    /// Ties the authentication cookie to a server-side session (BL-119): every cookie issued for a Stomme user carries
    /// the id of a session row opened for it, and a sign-out ends that row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One place for every sign-in.</b> The password form, the sign-in link, a passkey, SAML, Windows, impersonation
    /// and an external OIDC login all end in a cookie sign-in, so the cookie's own <c>SigningIn</c> event is the one seam
    /// that sees them all - no sign-in path can forget to open a session.
    /// </para>
    /// <para>
    /// <b>Fail closed.</b> When the session cannot be written, the sign-in fails: a cookie without a row would be the
    /// unrevocable cookie this exists to remove. A principal that is not a Stomme user (no integer user id) is left
    /// alone, as is a request with no resolved site.
    /// </para>
    /// </remarks>
    public static class SessionCookieEvents
    {
        public static async Task SigningInAsync(CookieSigningInContext context)
        {
            var registry = context.HttpContext.RequestServices.GetService<ISessionRegistry>();
            var config = context.HttpContext.RequestServices.GetService<IConfigManager>();
            var alias = context.HttpContext.GetAlias();
            var userId = context.Principal?.UserId() ?? -1;
            if (registry is null || config is null || !config.IsInstalled() || alias is null || userId <= 0)
            {
                return;
            }

            // The cookie this one replaces - a sign-in over an existing one, an impersonation - ends here.
            var previous = context.HttpContext.User?.SessionId();
            if (previous is { } replaced)
            {
                await registry.RevokeAsync(alias, replaced, null, "replaced by a new sign-in", context.HttpContext.User.UserId());
            }

            var expires = context.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow.Add(context.Options.ExpireTimeSpan);
            var sessionId = await registry.OpenAsync(alias, userId, expires);
            Stamp(context.Principal, sessionId);
        }

        public static async Task SigningOutAsync(CookieSigningOutContext context)
        {
            var principal = context.HttpContext.User;
            var sessionId = principal?.SessionId();
            var alias = context.HttpContext.GetAlias();
            var registry = context.HttpContext.RequestServices.GetService<ISessionRegistry>();
            if (sessionId is null || alias is null || registry is null)
            {
                return;
            }

            try
            {
                await registry.RevokeAsync(alias, sessionId.Value, principal.UserId(), "signed out", principal.UserId());
            }
            catch (Exception ex)
            {
                // The sign-out itself must still happen: the cookie is deleted either way, and a row left open simply
                // shows in the owner's list until it expires or they revoke it.
                context.HttpContext.RequestServices.GetService<ILogger<SessionRegistry>>()?
                    .LogError(ex, "Session {SessionId} could not be ended at sign-out.", sessionId);
            }
        }

        /// <summary>Puts the session id on the principal's Stomme identity, replacing any it carried.</summary>
        public static void Stamp(ClaimsPrincipal principal, Guid sessionId)
        {
            var identity = principal.Identities.FirstOrDefault(item => item.IsAuthenticated) ?? principal.Identities.First();
            foreach (var stale in identity.FindAll(Constants.SessionClaimType).ToList())
            {
                identity.RemoveClaim(stale);
            }

            identity.AddClaim(new Claim(Constants.SessionClaimType, sessionId.ToString()));
        }
    }
}
