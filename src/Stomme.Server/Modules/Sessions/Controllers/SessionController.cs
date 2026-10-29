#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stomme.Controllers;
using Stomme.Extensions;
using Stomme.Infrastructure;
using Stomme.Models.Sessions;
using Stomme.Modules.Sessions.Services;
using Stomme.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Stomme.Modules.Sessions.Controllers
{
    /// <summary>
    /// The sessions API. <c>GET api/Session</c> and <c>DELETE api/Session/{id}</c> are the caller's OWN sessions
    /// on this site; <c>GET api/Session/site</c> and <c>DELETE api/Session/site/{id}</c> are every session of the site,
    /// for Administrators. The site is always the request's own and the user always the caller - nothing here takes one.
    /// </summary>
    /// <remarks>
    /// Gated by FRAMEWORK policies (signed in; the Administrators role) and by no permission key - <c>identity.*</c> is
    /// the prefix D109 reserves, and who may end whose sign-in is the framework's question.
    /// </remarks>
    [Route(ControllerRoutes.ApiRoute)]
    public sealed class SessionController : ModuleControllerBase
    {
        private readonly ServerSessionService _sessions;

        public SessionController(ServerSessionService sessions, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _sessions = sessions;
        }

        [HttpGet]
        [Authorize]
        public Task<List<UserSessionView>> Get() => _sessions.GetMineAsync();

        [HttpDelete("{id}")]
        [Authorize]
        public async Task<IActionResult> Delete(Guid id)
        {
            if (!await _sessions.RevokeMineAsync(id))
            {
                return NotFound();
            }

            // Ending the session this very request came in on is signing out - say so to the browser too.
            if (User.SessionId() == id)
            {
                await HttpContext.SignOutAsync(Constants.AuthenticationScheme);
            }

            return NoContent();
        }

        [HttpGet("site")]
        [Authorize(Roles = RoleNames.Admin)]
        public Task<List<UserSessionView>> GetSite(int take) => _sessions.GetSiteAsync(take);

        [HttpDelete("site/{id}")]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<IActionResult> DeleteSite(Guid id) => await _sessions.RevokeAnyAsync(id) switch
        {
            RevokeOutcome.Revoked => NoContent(),
            RevokeOutcome.Forbidden => Forbid(),
            _ => NotFound(),
        };
    }
}
