using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Stomme.Controllers;
using Stomme.Enums;
using Stomme.FeatureManagement.Application;
using Stomme.Infrastructure;
using Stomme.Modules.FeatureManagement.Infrastructure;
using Stomme.Shared;

namespace Stomme.Modules.FeatureManagement.Controllers
{
    /// <summary>
    /// The feature-flag admin surface: list flags with their effective state, toggle them, set audience
    /// targeting and per-user overrides, and read the change history. Reads are open to administrators;
    /// every write is restricted to host users, because a flag change is installation-wide configuration.
    /// </summary>
    /// <remarks>
    /// Flags gate <em>capabilities</em>; the role/permission model gates <em>access</em> — including access to
    /// this controller. The two are deliberately not interchangeable: authorization here is by role, never by
    /// a feature flag, so a misconfigured flag can never lock an administrator out of fixing it.
    /// </remarks>
    [Route(ControllerRoutes.ApiRoute)]
    public class FeatureController : ModuleControllerBase
    {
        private readonly IFeatureAdminService _features;

        public FeatureController(IFeatureAdminService features, ILogManager logger, IHttpContextAccessor accessor)
            : base(logger, accessor)
        {
            _features = features;
        }

        // GET: api/feature
        [HttpGet]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<IReadOnlyList<FeatureView>> Get()
        {
            return await _features.ListAsync();
        }

        // GET: api/feature/audit?key=x&take=100
        [HttpGet("audit")]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<IReadOnlyList<FeatureAuditView>> GetAudit(string key, int take = 100)
        {
            return await _features.ListAuditsAsync(key, take);
        }

        // POST: api/feature/{key}
        [HttpPost("{key}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IActionResult> Toggle(string key, [FromBody] ToggleFeatureRequest request)
        {
            if (string.IsNullOrWhiteSpace(key) || request == null)
            {
                return BadRequest();
            }

            var id = await _features.ToggleAsync(key, request.Enabled);
            _logger.Log(LogLevel.Information, this, LogFunction.Update, "Feature Flag {Key} Set To {Enabled}", key, request.Enabled);
            return Ok(new { id });
        }

        // POST: api/feature/{key}/targeting
        [HttpPost("{key}/targeting")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IActionResult> SetTargeting(string key, [FromBody] SetTargetingRequest request)
        {
            if (string.IsNullOrWhiteSpace(key) || request == null)
            {
                return BadRequest();
            }

            try
            {
                var id = await _features.SetTargetingAsync(key, request.TargetRoles ?? [], request.RolloutPercentage);
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "Feature Flag {Key} Targeting Updated", key);
                return Ok(new { id });
            }
            catch (ArgumentOutOfRangeException)
            {
                return BadRequest();
            }
            catch (InvalidOperationException)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return NotFound();
            }
        }

        // POST: api/feature/{key}/override
        [HttpPost("{key}/override")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IActionResult> SetUserOverride(string key, [FromBody] SetUserOverrideRequest request)
        {
            if (string.IsNullOrWhiteSpace(key) || request == null || request.UserId <= 0)
            {
                return BadRequest();
            }

            try
            {
                // The admin surface speaks Stomme's integer user id; the aggregate keys overrides by the
                // projected GUID (see FeatureUserId).
                var id = await _features.SetUserOverrideAsync(key, FeatureUserId.FromUserId(request.UserId), request.Enabled);
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "Feature Flag {Key} Override Set For User {UserId}", key, request.UserId);
                return Ok(new { id });
            }
            catch (InvalidOperationException)
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return NotFound();
            }
        }
    }

    /// <summary>Body for toggling a feature flag over the REST surface.</summary>
    public class ToggleFeatureRequest
    {
        public bool Enabled { get; set; }
    }

    /// <summary>Body for replacing a flag's audience targeting.</summary>
    public class SetTargetingRequest
    {
        public List<string> TargetRoles { get; set; }

        public int RolloutPercentage { get; set; } = 100;
    }

    /// <summary>Body for setting (or, with a null <see cref="Enabled"/>, clearing) a per-user override.</summary>
    public class SetUserOverrideRequest
    {
        public int UserId { get; set; }

        public bool? Enabled { get; set; }
    }
}
