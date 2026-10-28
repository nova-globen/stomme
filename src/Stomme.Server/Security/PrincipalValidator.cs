using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.Cookies;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Extensions;
using Stomme.Shared;
using Stomme.Managers;
using Microsoft.AspNetCore.Authentication;
using Stomme.Modules.Sessions.Application;

namespace Stomme.Security
{
    public static class PrincipalValidator
    {
        public static async Task ValidateAsync(CookieValidatePrincipalContext context)
        {
            if (context != null && context.Principal.Identity.IsAuthenticated && context.Principal.Identity.Name != null)
            {
                var config = context.HttpContext.RequestServices.GetService(typeof(IConfigManager)) as IConfigManager;
                string path = context.Request.Path.ToString().ToLower();

                // check if framework is installed
                if (config.IsInstalled() && !path.StartsWith("/_")) // ignore Blazor framework requests
                {
                    var _logger = context.HttpContext.RequestServices.GetService(typeof(ILogManager)) as ILogManager;

                    var alias = context.HttpContext.GetAlias();
                    if (alias != null)
                    {
                        var userManager = context.HttpContext.RequestServices.GetService(typeof(IUserManager)) as IUserManager;
                        var user = await userManager.GetUserAsync(context.Principal.UserId(), alias.SiteId); // cached

                        // check if user is valid, not deleted, has roles, and security stamp has not changed for this tenant
                        if (user != null && !user.IsDeleted && !string.IsNullOrEmpty(user.Roles) && (context.Principal.SecurityStamp() == user.SecurityStamp || context.Principal.SiteKey() != alias.SiteKey))
                        {
                            // BL-119: the cookie's server-side session. Checked for a cookie of THIS site; one with no
                            // session (issued before sessions existed) or crossing from another site gets one below.
                            var sessions = context.HttpContext.RequestServices.GetService(typeof(ISessionRegistry)) as ISessionRegistry;
                            var sessionId = context.Principal.SessionId();
                            var crossing = context.Principal.SiteKey() != alias.SiteKey;
                            var mint = false;
                            if (sessions != null)
                            {
                                if (sessionId is { } id && !crossing)
                                {
                                    bool active;
                                    try
                                    {
                                        active = await sessions.IsActiveAsync(alias, id, user.UserId, context.Properties.ExpiresUtc);
                                    }
                                    catch (Exception ex)
                                    {
                                        // Fail closed for THIS request only: refused, but the cookie is kept, so a store
                                        // blip does not sign the user out for good.
                                        _logger.Log(alias.SiteId, LogLevel.Error, "UserValidation", Enums.LogFunction.Security, ex, "Session Could Not Be Checked For User {Username} Accessing {Url}", context.Principal.Identity.Name, path);
                                        context.RejectPrincipal();
                                        return;
                                    }

                                    if (!active)
                                    {
                                        Log(_logger, alias, "Session Ended For User {Username} Accessing {Url}", context.Principal.Identity.Name, path);
                                        context.RejectPrincipal();
                                        await context.HttpContext.SignOutAsync(Constants.AuthenticationScheme);
                                        return;
                                    }
                                }
                                else
                                {
                                    mint = true;
                                }
                            }

                            // validate security stamp and sitekey (in case user has changed tenants/sites in installation)
                            if (mint || context.Principal.SecurityStamp() != user.SecurityStamp || context.Principal.SiteKey() != alias.SiteKey || !context.Principal.Roles().Any())
                            {
                                // refresh principal - carrying its session, or opening one for a cookie that has none here
                                var identity = UserSecurity.CreateClaimsIdentity(alias, user);
                                var principal = new ClaimsPrincipal(identity);
                                if (mint)
                                {
                                    Guid opened;
                                    try
                                    {
                                        opened = await sessions.OpenAsync(alias, user.UserId, context.Properties.ExpiresUtc);
                                    }
                                    catch (Exception ex)
                                    {
                                        // No row, no cookie that could never be revoked: refused, cookie kept, tried again next request.
                                        _logger.Log(alias.SiteId, LogLevel.Error, "UserValidation", Enums.LogFunction.Security, ex, "Session Could Not Be Opened For User {Username} Accessing {Url}", context.Principal.Identity.Name, path);
                                        context.RejectPrincipal();
                                        return;
                                    }

                                    SessionCookieEvents.Stamp(principal, opened);
                                }
                                else if (sessionId is { } carried)
                                {
                                    SessionCookieEvents.Stamp(principal, carried);
                                }

                                context.ReplacePrincipal(principal);
                                context.ShouldRenew = true;
                                Log(_logger, alias, "Permissions Refreshed For User {Username} Accessing {Url}", context.Principal.Identity.Name, path);
                            }
                        }
                        else
                        {
                            // remove principal (ie. log user out)
                            Log(_logger, alias, "Permissions Removed For User {Username} Accessing {Url}", context.Principal.Identity.Name, path);
                            await EndSessionAsync(context, alias, "account changed");
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(Constants.AuthenticationScheme);
                        }
                    }
                    else
                    {
                        // user is signed in but site cannot be determined
                        Log(_logger, alias, "Alias Could Not Be Resolved For User {Username} Accessing {Url}", context.Principal.Identity.Name, path);
                    }
                }
            }
        }

        // BL-119: a cookie refused because its account changed (stamp, deletion, roles) ends its session row too, so the
        // owner's list does not keep showing a session no cookie can use. Best effort - the refusal stands either way.
        private static async Task EndSessionAsync(CookieValidatePrincipalContext context, Alias alias, string reason)
        {
            var sessionId = context.Principal.SessionId();
            if (sessionId is null || context.Principal.SiteKey() != alias.SiteKey
                || context.HttpContext.RequestServices.GetService(typeof(ISessionRegistry)) is not ISessionRegistry sessions)
            {
                return;
            }

            try
            {
                await sessions.RevokeAsync(alias, sessionId.Value, null, reason, context.Principal.UserId());
            }
            catch (Exception)
            {
                // the row simply shows until it expires
            }
        }

        private static void Log (ILogManager logger, Alias alias, string message, string username, string path)
        {
            if (!path.StartsWith("/api/")) // reduce log verbosity
            {
                var siteId = (alias != null) ? alias.SiteId : -1;
                logger.Log(siteId, LogLevel.Information, "UserValidation", Enums.LogFunction.Security, message, username, path);
            }
        }
    }
}
