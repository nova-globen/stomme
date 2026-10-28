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
    public class SiteTaskController : Controller
    {
        private readonly ISiteTaskRepository _siteTasks;
        private readonly ILogManager _logger;
        private readonly ITenantManager _tenantManager;

        public SiteTaskController(ISiteTaskRepository siteTasks, ILogManager logger, ITenantManager tenantManager)
        {
            _siteTasks = siteTasks;
            _logger = logger;
            _tenantManager = tenantManager;
        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<SiteTask> Get(int id)
        {
            var alias = await _tenantManager.GetAliasAsync();
            var siteTask = await _siteTasks.GetSiteTaskAsync(id);
            if (siteTask.SiteId == alias.SiteId)
            {
                return siteTask;
            }
            else
            {
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                return null;
            }
        }

        // POST api/<controller>
        [HttpPost]
        [Authorize(Roles = RoleNames.Admin)]
        public async Task<SiteTask> Post([FromBody] SiteTask siteTask)
        {
            var alias = await _tenantManager.GetAliasAsync();
            if (ModelState.IsValid && siteTask.SiteId == alias.SiteId)
            {
                siteTask.IsCompleted = false;
                siteTask = await _siteTasks.AddSiteTaskAsync(siteTask);
                _logger.Log(LogLevel.Information, this, LogFunction.Create, "Site Task Added {SiteTask}", siteTask);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Site Task Post Attempt {SiteTask}", siteTask);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                siteTask = null;
            }
            return siteTask;
        }
    }
}
