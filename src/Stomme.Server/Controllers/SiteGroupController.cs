using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Policy;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class SiteGroupController : Controller
    {
        private readonly ISiteGroupRepository _siteGroupRepository;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;

        public SiteGroupController(ISiteGroupRepository siteGroupRepository, ISyncManager syncManager, ILogManager logger, ITenantManager tenantManager)
        {
            _siteGroupRepository = siteGroupRepository;
            _syncManager = syncManager;
            _logger = logger;
            _tenantManager = tenantManager;
        }

        // GET: api/<controller>?siteid=x
        [HttpGet]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<IEnumerable<SiteGroup>> Get(string siteid)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (User.IsInRole(RoleNames.Host) || (int.TryParse(siteid, out int SiteId) && SiteId == alias.SiteId))
            {
                var siteGroups = await _siteGroupRepository.GetSiteGroupsAsync();
                if (!User.IsInRole(RoleNames.Host))
                {
                    siteGroups = siteGroups.Where(item => item.PrimarySiteId == alias.SiteId);
                }
                return siteGroups.ToList();
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Site Group Get Attempt {SiteId}", siteid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<SiteGroup> Get(int id)
        {
            var group = await _siteGroupRepository.GetSiteGroupAsync(id);
            if (group != null)
            {
                return group;
            }
            else
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                return null;
            }
        }

        // POST api/<controller>
        [HttpPost]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<SiteGroup> Post([FromBody] SiteGroup siteGroup)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (ModelState.IsValid)
            {
                siteGroup = await _siteGroupRepository.AddSiteGroupAsync(siteGroup);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.SiteGroup, siteGroup.SiteGroupId, SyncEventActions.Create);
                _logger.Log(LogLevel.Information, this, LogFunction.Create, "Site Group Added {Group}", siteGroup);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Site Group Post Attempt {Group}", siteGroup);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                siteGroup = null;
            }
            return siteGroup;
        }

        // PUT api/<controller>/5
        [HttpPut("{id}")]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<SiteGroup> Put(int id, [FromBody] SiteGroup siteGroup)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (ModelState.IsValid && siteGroup.SiteGroupId == id)
            {
                if (!User.IsInRole(RoleNames.Host) && siteGroup.Synchronize)
                {
                    // admins can only update the synchronize field
                    siteGroup = await _siteGroupRepository.GetSiteGroupAsync(siteGroup.SiteGroupId, false);
                    siteGroup.Synchronize = true;
                }
                siteGroup = await _siteGroupRepository.UpdateSiteGroupAsync(siteGroup);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.SiteGroup, siteGroup.SiteGroupId, SyncEventActions.Update);
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "Site Group Updated {Group}", siteGroup);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Site Group Put Attempt {Group}", siteGroup);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                siteGroup = null;
            }
            return siteGroup;
        }

        // DELETE api/<controller>/5
        [HttpDelete("{id}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task Delete(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var siteGroup = await _siteGroupRepository.GetSiteGroupAsync(id);
            if (siteGroup != null)
            {
                await _siteGroupRepository.DeleteSiteGroupAsync(id);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.SiteGroup, siteGroup.SiteGroupId, SyncEventActions.Delete);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Site Group Deleted {siteGroupId}", id);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Site Group Delete Attempt {siteGroupId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }
    }
}
