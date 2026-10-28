using Microsoft.AspNetCore.Mvc;
using Stomme.Models;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Stomme.Infrastructure;
using Stomme.Repository;
using Stomme.Shared;
using Stomme.Enums;
using System.Net;

namespace Stomme.Controllers
{

    [Route(ControllerRoutes.ApiRoute)]
    public class LogController : Controller
    {
        private readonly ILogManager _logger;
        private readonly ILogRepository _logs;
        private readonly ITenantManager _tenantManager;

        public LogController(ILogManager logger, ILogRepository logs, ITenantManager tenantManager)
        {
            _logger = logger;
            _logs = logs;
            _tenantManager = tenantManager;
        }

        // GET: api/<controller>?siteid=x&level=y&function=z&rows=50
        [HttpGet]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<IEnumerable<Log>> Get(string siteid, string level, string function, string rows)
        {
            var alias = await _tenantManager.GetAliasAsync();
            int SiteId;
            if (int.TryParse(siteid, out SiteId) && SiteId == alias.SiteId)
            {
                return await _logs.GetLogsAsync(SiteId, level, function, int.Parse(rows));
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Log Get Attempt {SiteId}", siteid);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }

        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<Log> Get(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var log = await _logs.GetLogAsync(id);
            if (log != null && log.SiteId == alias.SiteId)
            {
                return log;
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Log Get Attempt {LogId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        // POST api/<controller>
        [HttpPost]
        public async Task Post([FromBody] Log log)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (ModelState.IsValid && log.SiteId == alias.SiteId)
            {
                _logger.Log(log);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Log Post Attempt {Log}", log);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }

        [HttpDelete]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task Delete(string siteId)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (int.TryParse(siteId, out int parsedSiteId) && parsedSiteId == alias.SiteId)
            {
                await _logs.DeleteLogsAsync(parsedSiteId, 0); // specifying zero for age results in all logs being deleted
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Log Delete Attempt {SiteId}", siteId);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }
    }
}
