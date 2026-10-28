using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Stomme.Enums;
using Stomme.Models;
using Stomme.Shared;
using Stomme.Infrastructure;
using Stomme.Repository;
using System.Net;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class ProfileController : Controller
    {
        private readonly IProfileRepository _profiles;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;

        public ProfileController(IProfileRepository profiles, ISyncManager syncManager, ILogManager logger, ITenantManager tenantManager)
        {
            _profiles = profiles;
            _syncManager = syncManager;
            _logger = logger;
            _tenantManager = tenantManager;
        }

        // GET: api/<controller>?siteid=x
        [HttpGet]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<IEnumerable<Profile>> Get(string siteid)
        {
            var alias = await _tenantManager.GetAliasAsync();
            int SiteId;
            if (int.TryParse(siteid, out SiteId) && SiteId == alias.SiteId)
            {
                return await _profiles.GetProfilesAsync(SiteId);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Profile Get Attempt {SiteId}", siteid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        [Authorize(Roles = RoleNames.Registered)]
        public async Task<Profile> Get(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var profile = await _profiles.GetProfileAsync(id);
            if (profile != null && profile.SiteId == alias.SiteId)
            {
                return profile;
            }
            else
            {
                if (profile != null)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Profile Get Attempt {ProfileId}", id);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
                else
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
                return null;
            }
        }

        // POST api/<controller>
        [HttpPost]
        [Authorize(Policy = $"{EntityNames.Profile}:{PermissionNames.Write}:{RoleNames.Admin}")]
        public async Task<Profile> Post([FromBody] Profile profile)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (IsBlank(profile))
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Create, "Profile Create Rejected - The Name Or Title Is Blank");
                HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return null;
            }

            if (ModelState.IsValid && profile.SiteId == alias.SiteId)
            {
                profile = await _profiles.AddProfileAsync(profile);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Profile, profile.ProfileId, SyncEventActions.Create);
                _logger.Log(LogLevel.Information, this, LogFunction.Create, "Profile Added {Profile}", profile);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Profile Post Attempt {Profile}", profile);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                profile = null;
            }
            return profile;
        }

        // PUT api/<controller>/5
        [HttpPut("{id}")]
        [Authorize(Policy = $"{EntityNames.Profile}:{PermissionNames.Write}:{RoleNames.Admin}")]
        public async Task<Profile> Put(int id, [FromBody] Profile profile)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (IsBlank(profile))
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Update, "Profile Update Rejected - The Name Or Title Is Blank");
                HttpContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return null;
            }

            if (ModelState.IsValid && profile.SiteId == alias.SiteId && profile.ProfileId == id && await _profiles.GetProfileAsync(profile.ProfileId, false) != null)
            {
                profile = await _profiles.UpdateProfileAsync(profile);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Profile, profile.ProfileId, SyncEventActions.Update);
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "Profile Updated {Profile}", profile);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Profile Put Attempt {Profile}", profile);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                profile = null;
            }
            return profile;
        }

        // BL-143 (the BL-87 class): the admin form marks both required, but the server checked only ModelState and the model
        // carries no annotation, so a nameless or untitled profile item could be written through the API - measured, 200
        // and a row. The name is the item's key as well as its label.
        private static bool IsBlank(Profile profile) =>
            string.IsNullOrWhiteSpace(profile?.Name) || string.IsNullOrWhiteSpace(profile.Title);

        // DELETE api/<controller>/5
        [HttpDelete("{id}")]
        [Authorize(Policy = $"{EntityNames.Profile}:{PermissionNames.Write}:{RoleNames.Admin}")]
        public async Task Delete(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var profile = await _profiles.GetProfileAsync(id);
            if (profile != null && profile.SiteId == alias.SiteId)
            {
                await _profiles.DeleteProfileAsync(id);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Profile, profile.ProfileId, SyncEventActions.Delete);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Profile Deleted {ProfileId}", id);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Profile Delete Attempt {ProfileId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }
    }
}
