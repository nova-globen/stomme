using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Stomme.Models;
using Stomme.Shared;
using System;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Enums;
using Stomme.Infrastructure;
using Stomme.Repository;
using System.Net;

namespace Stomme.Controllers
{
    [Route(ControllerRoutes.ApiRoute)]
    public class JobController : Controller
    {
        private readonly IJobRepository _jobs;
        private readonly ILogManager _logger;
        private readonly IServiceProvider _serviceProvider;

        public JobController(IJobRepository jobs, ILogManager logger, IServiceProvider serviceProvider)
        {
            _jobs = jobs;
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        // GET: api/<controller>
        [HttpGet]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<IEnumerable<Job>> Get()
        {
            return await _jobs.GetJobsAsync();
        }

        // GET api/<controller>/5
        [HttpGet("{id}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<Job> Get(int id)
        {
            return await _jobs.GetJobAsync(id);
        }

        // POST api/<controller>
        [HttpPost]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<Job> Post([FromBody] Job job)
        {
            if (ModelState.IsValid)
            {
                job = await _jobs.AddJobAsync(job);
                _logger.Log(LogLevel.Information, this, LogFunction.Create, "Job Added {Job}", job);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Job Post Attempt {Job}", job);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                job = null;
            }
            return job;
        }

        // PUT api/<controller>/5
        [HttpPut("{id}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task<Job> Put(int id, [FromBody] Job job)
        {
            if (ModelState.IsValid && job.JobId == id && await _jobs.GetJobAsync(job.JobId, false) != null)
            {
                job = await _jobs.UpdateJobAsync(job);
                _logger.Log(LogLevel.Information, this, LogFunction.Update, "Job Updated {Job}", job);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Job Put Attempt {Job}", job);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                job = null;
            }
            return job;
        }

        // DELETE api/<controller>/5
        [HttpDelete("{id}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task Delete(int id)
        {
            var job = await _jobs.GetJobAsync(id);
            if (job != null)
            {
                await _jobs.DeleteJobAsync(id);
                _logger.Log(LogLevel.Information, this, LogFunction.Delete, "Job Deleted {JobId}", id);
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Job Delete Attempt {JobId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }

        // GET api/<controller>/start
        [HttpGet("start/{id}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task Start(int id)
        {
            Job job = await _jobs.GetJobAsync(id);
            if (job != null && Type.GetType(job.JobType) is { } jobtype)
            {
                var jobobject = ActivatorUtilities.CreateInstance(_serviceProvider, jobtype);
                _ = ((IHostedService)jobobject).StartAsync(new System.Threading.CancellationToken());
            }
            else if (job != null)
            {
                // The module that brought the job is gone (listing the jobs deletes such a row): nothing to start - it was a 500.
                _logger.Log(LogLevel.Warning, this, LogFunction.Other, "Job {JobId} Cannot Start - Its Type {JobType} Is Not Installed", id, job.JobType);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Conflict;
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Job Start Attempt {JobId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }

        // GET api/<controller>/stop
        [HttpGet("stop/{id}")]
        [Authorize(Roles = RoleNames.Host)]
        public async Task Stop(int id)
        {
            Job job = await _jobs.GetJobAsync(id);
            if (job != null && Type.GetType(job.JobType) is { } jobtype)
            {
                var jobobject = ActivatorUtilities.CreateInstance(_serviceProvider, jobtype);
                _ = ((IHostedService)jobobject).StopAsync(new System.Threading.CancellationToken());
            }
            else if (job != null)
            {
                _logger.Log(LogLevel.Warning, this, LogFunction.Other, "Job {JobId} Cannot Stop - Its Type {JobType} Is Not Installed", id, job.JobType);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Conflict;
            }
            else
            {
                _logger.Log(LogLevel.Error, this, LogFunction.Security, "Unauthorized Job Stop Attempt {JobId}", id);
                HttpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            }
        }
    }
}
