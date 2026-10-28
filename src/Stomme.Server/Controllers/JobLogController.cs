using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Stomme.Models;
using Stomme.Shared;
using Stomme.Repository;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class JobLogController : Controller
    {
        private readonly IJobLogRepository _jobLogs;

        public JobLogController(IJobLogRepository jobLogs)
        {
            _jobLogs = jobLogs;
        }

        // GET: api/<controller>?jobid=x
        [HttpGet]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IEnumerable<JobLog>> Get(string jobid)
        {
            return await _jobLogs.GetJobLogsAsync(int.Parse(jobid));
        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<JobLog> Get(int id)
        {
            return await _jobLogs.GetJobLogAsync(id);
        }
    }
}
