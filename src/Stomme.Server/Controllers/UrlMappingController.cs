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
    public class UrlMappingController : Controller
    {
        private readonly IUrlMappingRepository _urlMappings;
        private readonly ISyncManager _syncManager;
        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;

        public UrlMappingController(IUrlMappingRepository urlMappings, ISyncManager syncManager, ILogManager logger, ITenantManager tenantManager)
        {
            _urlMappings = urlMappings;
            _syncManager = syncManager;
            _logger = logger;
            _tenantManager = tenantManager;
        }

        // GET: api/<controller>?siteid=x&ismapped=y
        [HttpGet]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<IEnumerable<UrlMapping>> Get(string siteid, string ismapped)
        {
            var alias = await _tenantManager.GetAliasAsync();
            int SiteId;
            if (int.TryParse(siteid, out SiteId) && SiteId == alias.SiteId)
            {
                return await _urlMappings.GetUrlMappingsAsync(SiteId, bool.Parse(ismapped));
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized UrlMapping Get Attempt {SiteId}", siteid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<UrlMapping> Get(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var urlMapping = await _urlMappings.GetUrlMappingAsync(id);
            if (urlMapping != null && urlMapping.SiteId == alias.SiteId)
            {
                return urlMapping;
            }
            else
            {
                if (urlMapping != null)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized UrlMapping Get Attempt {UrlMappingId}", id);
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                }
                else
                {
                    HttpContext.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
                return null;
            }
        }

        // GET api/<controller>/url/x?url=y
        [HttpGet("url/{siteid}")]
        public async Task<UrlMapping> Get(int siteid, string url)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var urlMapping = await _urlMappings.GetUrlMappingAsync(siteid, WebUtility.UrlDecode(url));
            if (urlMapping != null && urlMapping.SiteId == alias.SiteId)
            {
                return urlMapping;
            }
            else
            {
                if (urlMapping != null)
                {
                    _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized UrlMapping Get Attempt {SiteId} {Url}", siteid, url);
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
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<UrlMapping> Post([FromBody] UrlMapping urlMapping)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (ModelState.IsValid && urlMapping.SiteId == alias.SiteId)
            {
                urlMapping = await _urlMappings.AddUrlMappingAsync(urlMapping);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.UrlMapping, urlMapping.UrlMappingId, SyncEventActions.Create);
                _logger.Log(LogLevel.Information, this, LogFunction.Create, "UrlMapping Added {UrlMapping}", urlMapping);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized UrlMapping Post Attempt {Role}", urlMapping);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                urlMapping = null;
            }
            return urlMapping;
        }

        // PUT api/<controller>/5
        [HttpPut("{id}")]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<UrlMapping> Put(int id, [FromBody] UrlMapping urlMapping)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (ModelState.IsValid && urlMapping.SiteId == alias.SiteId && urlMapping.UrlMappingId == id && await _urlMappings.GetUrlMappingAsync(urlMapping.UrlMappingId, false) != null)
            {
                urlMapping = await _urlMappings.UpdateUrlMappingAsync(urlMapping);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.UrlMapping, urlMapping.UrlMappingId, SyncEventActions.Update);
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "UrlMapping Updated {UrlMapping}", urlMapping);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized UrlMapping Put Attempt {UrlMapping}", urlMapping);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                urlMapping = null;
            }
            return urlMapping;
        }

        // DELETE api/<controller>/5
        [HttpDelete("{id}")]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task Delete(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var urlMapping = await _urlMappings.GetUrlMappingAsync(id);
            if (urlMapping != null && urlMapping.SiteId == alias.SiteId)
            {
                await _urlMappings.DeleteUrlMappingAsync(id);
                await _syncManager.AddSyncEventAsync(alias, EntityNames.UrlMapping, urlMapping.UrlMappingId, SyncEventActions.Delete);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "UrlMapping Deleted {UrlMappingId}", id);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized UrlMapping Delete Attempt {UrlMappingId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }
    }
}
