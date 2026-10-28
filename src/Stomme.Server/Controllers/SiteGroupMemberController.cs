using System.Collections.Generic;
using System.Linq;
using System.Net;
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
    public class SiteGroupMemberController : Controller
    {
        private readonly ISiteGroupMemberRepository _siteGroupMemberRepository;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;

        public SiteGroupMemberController(ISiteGroupMemberRepository siteGroupMemberRepository, ISyncManager syncManager, ILogManager logger, ITenantManager tenantManager)
        {
            _siteGroupMemberRepository = siteGroupMemberRepository;
            _syncManager = syncManager;
            _logger = logger;
            _tenantManager = tenantManager;
        }

        // GET: api/<controller>?siteid=x&groupid=y
        [HttpGet]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IEnumerable<SiteGroupMember>> Get(string siteid, string groupid)
        {
            if (int.TryParse(siteid, out int SiteId) && int.TryParse(groupid, out int SiteGroupId))
            {
                return (await _siteGroupMemberRepository.GetSiteGroupMembersAsync(SiteId, SiteGroupId)).ToList();
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Site Group Member Get Attempt for SiteId {SiteId} And SiteGroupId {SiteGroupId}", siteid, groupid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<SiteGroupMember> Get(int id)
        {
            var siteGroupMember = await _siteGroupMemberRepository.GetSiteGroupMemberAsync(id);
            if (siteGroupMember != null)
            {
                return siteGroupMember;
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
        public async Task<SiteGroupMember> Post([FromBody] SiteGroupMember siteGroupMember)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (ModelState.IsValid)
            {
                siteGroupMember = await _siteGroupMemberRepository.AddSiteGroupMemberAsync(siteGroupMember);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.SiteGroupMember, siteGroupMember.SiteGroupId, SyncEventActions.Create);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, siteGroupMember.SiteId, SyncEventActions.Refresh);
                _logger.Log(LogLevel.Information, this, LogFunction.Create, "Site Group Member Added {SiteGroupMember}", siteGroupMember);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Site Group Member Post Attempt {SiteGroupMember}", siteGroupMember);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                siteGroupMember = null;
            }
            return siteGroupMember;
        }

        // PUT api/<controller>/5
        [HttpPut("{id}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<SiteGroupMember> Put(int id, [FromBody] SiteGroupMember siteGroupMember)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (ModelState.IsValid && siteGroupMember.SiteGroupId == id && await _siteGroupMemberRepository.GetSiteGroupMemberAsync(siteGroupMember.SiteGroupId, false) != null)
            {
                siteGroupMember = await _siteGroupMemberRepository.UpdateSiteGroupMemberAsync(siteGroupMember);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.SiteGroupMember, siteGroupMember.SiteGroupId, SyncEventActions.Update);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, siteGroupMember.SiteId, SyncEventActions.Refresh);
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "Site Group Member Updated {SiteGroupMember}", siteGroupMember);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Site Group Member Put Attempt {SiteGroupMember}", siteGroupMember);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                siteGroupMember = null;
            }
            return siteGroupMember;
        }

        // DELETE api/<controller>/5
        [HttpDelete("{id}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task Delete(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var siteGroupMember = await _siteGroupMemberRepository.GetSiteGroupMemberAsync(id);
            if (siteGroupMember != null)
            {
                await _siteGroupMemberRepository.DeleteSiteGroupMemberAsync(id);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.SiteGroupMember, siteGroupMember.SiteGroupMemberId, SyncEventActions.Delete);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.Site, siteGroupMember.SiteId, SyncEventActions.Refresh);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Site Group Member Deleted {SiteGroupMemberId}", id);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Site Group Member Delete Attempt {SiteGroupMemberId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }
    }
}
