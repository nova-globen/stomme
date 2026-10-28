using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Stomme.Models;
using Stomme.Infrastructure.Tenancy;
using Stomme.Persistence.Abstractions;
using Stomme.Repository;
using Stomme.Tenancy.Domain;
using Stomme.Shared;

namespace Stomme.Infrastructure
{
    public abstract class HostedServiceBase : IHostedService, IDisposable
    {
        private Task _executingTask;
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public HostedServiceBase(IServiceScopeFactory serviceScopeFactory)
        {
            _serviceScopeFactory = serviceScopeFactory;
        }

        // public properties which can be overridden and are used during auto registration of job
        public string Name { get; set; } = "";
        public string Frequency { get; set; } = "d"; // day
        public int Interval { get; set; } = 1;
        public DateTime? StartDate { get; set; } = null;
        public DateTime? EndDate { get; set; } = null;
        public int RetentionHistory { get; set; } = 10;
        public bool IsEnabled { get; set; } = false;
        public int MaximumDuration { get; set; } = 30; // minutes

        // one of the following methods must be overridden
        public virtual string ExecuteJob(IServiceProvider provider)
        {
            return "";
        }

        public virtual Task<string> ExecuteJobAsync(IServiceProvider provider)
        {
            return Task.FromResult(string.Empty);
        }


        /// <summary>
        /// Runs the job once per tenant, each in its OWN tenant scope and DI scope, each failure contained (BL-107).
        /// </summary>
        /// <remarks>
        /// <para>
        /// The loop used to run every tenant on ONE DI scope, with <c>TenantManager.SetTenant</c> as the only
        /// switch and a single catch around the lot. Two consequences, both measured by reading the code this
        /// fix is pinned by: scoped services that resolve the tenant ONCE per scope - <c>StommeTenantContext</c>
        /// caches the alias on first read, and <c>FeatureEvaluator</c> depends on it - kept answering for tenant
        /// ONE for every later tenant, so a per-tenant flag like VisitorTracking was read from the wrong tenant;
        /// and one tenant throwing skipped every LATER tenant and replaced the notes of the tenants already done
        /// with the exception message alone.
        /// </para>
        /// <para>
        /// Each tenant now gets <see cref="ITenantScopeFactory.BeginScope"/> (the ambient tenant, which the tenant
        /// context reads FRESH and never caches) around a new DI scope, plus <c>SetTenant</c> for the alias-driven
        /// framework repositories. A tenant whose id is not a framework tenant id is skipped loudly rather than
        /// run in SYSTEM scope, which would read every tenant's rows. The job's own cancellation token is passed
        /// down at last, so a stopping host stops between tenants.
        /// </para>
        /// </remarks>
        internal async Task<(string Notes, bool Succeeded)> RunTenantsAsync(IEnumerable<Tenant> tenants, CancellationToken stoppingToken)
        {
            var notes = "";
            var succeeded = true;

            // The tenant-scope factory is a singleton, but it is resolved from a scope like everything else; this
            // scope exists only to reach it, and holds no tenant-dependent service.
            using var factoryScope = _serviceScopeFactory.CreateScope();
            var tenantScopes = factoryScope.ServiceProvider.GetRequiredService<ITenantScopeFactory>();

            foreach (var tenant in tenants)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    notes += $"Stopping - {tenant.Name} and any later tenant were not processed<br />";
                    break;
                }

                var tenantId = FrameworkTenantId.ToTenantIdOrNull(tenant.TenantId);
                if (tenantId is null)
                {
                    notes += $"Skipped {tenant.Name}: {tenant.TenantId} is not a framework tenant id<br />";
                    succeeded = false;
                    continue;
                }

                try
                {
                    using var tenantScope = tenantScopes.BeginScope(tenantId.Value);
                    using var jobScope = _serviceScopeFactory.CreateScope();
                    jobScope.ServiceProvider.GetRequiredService<ITenantManager>().SetTenant(tenant);

                    notes += ExecuteJob(jobScope.ServiceProvider);
                    notes += await ExecuteJobAsync(jobScope.ServiceProvider);
                }
                catch (Exception ex)
                {
                    // Contained per tenant: the tenants already processed keep their notes, and the ones after
                    // this still run.
                    notes += $"Error Processing {tenant.Name} - {ex.Message}<br />";
                    succeeded = false;
                }
            }

            return (notes, succeeded);
        }
        protected async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield(); // required so that this method does not block startup

            // random delay to prevent jobs from running concurrently (ie. thundering herd)
            int randomDelay = new Random().Next(0, 10000); 
            await Task.Delay(randomDelay, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    IConfigurationRoot _config = scope.ServiceProvider.GetRequiredService<IConfigurationRoot>();
                    ILogger<HostedServiceBase> _filelogger = scope.ServiceProvider.GetRequiredService<ILogger<HostedServiceBase>>();

                    // if framework is installed
                    if (IsInstalled(_config))
                    {
                        try
                        {
                            // get required services
                            var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
                            var jobLogs = scope.ServiceProvider.GetRequiredService<IJobLogRepository>();
                            var tenantRepository = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
                            var tenantManager = scope.ServiceProvider.GetRequiredService<ITenantManager>();

                            // get name of job
                            string jobTypeName = Utilities.GetFullTypeName(GetType().AssemblyQualifiedName);

                            // load current job
                            Job job = await jobs.GetJobAsync(jobTypeName);

                            if (job == null)
                            {
                                // auto registration
                                job = new Job { JobType = jobTypeName };

                                // optional HostedServiceBase properties
                                var jobType = Type.GetType(jobTypeName);
                                var jobObject = ActivatorUtilities.CreateInstance(scope.ServiceProvider, jobType) as HostedServiceBase;
                                if (jobObject.Name != "")
                                {
                                    job.Name = jobObject.Name;
                                }
                                else
                                {
                                    job.Name = Utilities.GetTypeName(job.JobType);
                                }
                                job.Frequency = jobObject.Frequency;
                                job.Interval = jobObject.Interval;
                                job.StartDate = jobObject.StartDate;
                                job.EndDate = jobObject.EndDate;
                                job.MaximumDuration = jobObject.MaximumDuration;
                                job.RetentionHistory = jobObject.RetentionHistory;
                                job.IsEnabled = jobObject.IsEnabled;
                                job.IsStarted = true;
                                job.IsExecuting = false;
                                job.NextExecution = null;

                                try
                                {
                                    job = await jobs.AddJobAsync(job);
                                }
                                catch
                                {
                                    // ignore exception which can occur if multiple instances are trying to auto register the same job
                                    job = await jobs.GetJobAsync(jobTypeName);
                                }
                            }

                            if (job != null && job.IsEnabled)
                            {
                                // get next execution date
                                DateTime NextExecution;
                                if (job.NextExecution == null)
                                {
                                    if (job.StartDate != null)
                                    {
                                        NextExecution = job.StartDate.Value;
                                    }
                                    else
                                    {
                                        NextExecution = DateTime.UtcNow;
                                    }
                                }
                                else
                                {
                                    NextExecution = job.NextExecution.Value;
                                }
                                NextExecution = RemoveSeconds(NextExecution);

                                // auto healing if a job has been executing longer than the maximum duration (ie. due to an exception or forceful termination)
                                if (job.IsExecuting && RemoveSeconds(DateTime.UtcNow) > NextExecution.AddMinutes(job.MaximumDuration))
                                {
                                    // reset job
                                    job.IsExecuting = false;
                                }

                                // determine if the job should be run
                                if (!job.IsExecuting && NextExecution <= RemoveSeconds(DateTime.UtcNow) && (job.EndDate == null || job.EndDate >= RemoveSeconds(DateTime.UtcNow)))
                                {
                                    // update the job to indicate it is executing (prevents multiple instances of the same job running concurrently)
                                    job.IsExecuting = true;
                                    job = await jobs.UpdateJobAsync(job);

                                    // create a job log entry
                                    JobLog log = new JobLog();
                                    log.JobId = job.JobId;
                                    log.StartDate = DateTime.UtcNow;
                                    log.FinishDate = null;
                                    log.Succeeded = false;
                                    log.Notes = "";
                                    log.Server = Environment.MachineName;
                                    log.Instance = Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") ?? "";
                                    log = await jobLogs.AddJobLogAsync(log);

                                    // execute the job
                                    var run = await RunTenantsAsync(await tenantRepository.GetTenantsAsync(), stoppingToken);
                                    log.Notes = run.Notes;
                                    log.Succeeded = run.Succeeded;

                                    // update the job log
                                    log.FinishDate = DateTime.UtcNow;
                                    await jobLogs.UpdateJobLogAsync(log);

                                    // update the job
                                    job.NextExecution = CalculateNextExecution(NextExecution, job);
                                    if (job.Frequency == "O") // one time
                                    {
                                        job.EndDate = DateTime.UtcNow;
                                        job.NextExecution = null;
                                    }
                                    job.IsExecuting = false;
                                    await jobs.UpdateJobAsync(job);

                                    // trim the job log
                                    List<JobLog> logs = (await jobLogs.GetJobLogsAsync(job.JobId)).ToList();
                                    for (int i = logs.Count; i > job.RetentionHistory; i--)
                                    {
                                        await jobLogs.DeleteJobLogAsync(logs[i - 1].JobLogId);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _filelogger.LogError(Utilities.LogMessage(this, $"An Error Occurred Executing Scheduled Job: {Name} - {ex}"));
                        }
                    }
                }

                // wait 1 minute
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }

        private DateTime CalculateNextExecution(DateTime nextExecution, Job job)
        {
            switch (job.Frequency)
            {
                case "m": // minutes
                    nextExecution = nextExecution.AddMinutes(job.Interval);
                    if (nextExecution < DateTime.UtcNow) nextExecution = DateTime.UtcNow;
                    break;
                case "H": // hours
                    nextExecution = nextExecution.AddHours(job.Interval);
                    if (nextExecution < DateTime.UtcNow) nextExecution = DateTime.UtcNow;
                    break;
                case "d": // days
                    nextExecution = DateTime.UtcNow.Date.Add(nextExecution.TimeOfDay); // preserve time of day
                    nextExecution = nextExecution.AddDays(job.Interval);
                    break;
                case "w": // weeks
                    nextExecution = DateTime.UtcNow.Date.Add(nextExecution.TimeOfDay); // preserve time of day
                    nextExecution = nextExecution.AddDays(job.Interval * 7);
                    break;
                case "M": // months
                    nextExecution = DateTime.UtcNow.Date.Add(nextExecution.TimeOfDay); // preserve time of day
                    nextExecution = nextExecution.AddMonths(job.Interval);
                    break;
                case "O": // one time
                    break;
            }
            return RemoveSeconds(nextExecution);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                IConfigurationRoot _config = scope.ServiceProvider.GetRequiredService<IConfigurationRoot>();
                ILogger<HostedServiceBase> _filelogger = scope.ServiceProvider.GetRequiredService<ILogger<HostedServiceBase>>();

                try
                {
                    if (IsInstalled(_config))
                    {
                        string jobTypeName = Utilities.GetFullTypeName(GetType().AssemblyQualifiedName);
                        IJobRepository jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
                        Job job = await jobs.GetJobAsync(jobTypeName);
                        if (job != null)
                        {
                            job.IsStarted = true;
                            await jobs.UpdateJobAsync(job);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _filelogger.LogError(Utilities.LogMessage(this, $"An Error Occurred Starting Scheduled Job: {Name} - {ex}"));
                }
            }

            _executingTask = ExecuteAsync(_cancellationTokenSource.Token);

            if (_executingTask.IsCompleted)
            {
                // surface a failure which occurred before the first suspension point to the host
                await _executingTask;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            using (var scope = _serviceScopeFactory.CreateScope())
            {
                ILogger<HostedServiceBase> _filelogger = scope.ServiceProvider.GetRequiredService<ILogger<HostedServiceBase>>();

                try
                {
                    string jobTypeName = Utilities.GetFullTypeName(GetType().AssemblyQualifiedName);
                    IJobRepository jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
                    Job job = await jobs.GetJobAsync(jobTypeName);
                    if (job != null)
                    {
                        // reset job 
                        job.IsStarted = false; // note that in a scale out environment this only affects the current instance, not other instances
                        await jobs.UpdateJobAsync(job);
                    }
                }
                catch (Exception ex)
                {
                    // error updating the job
                    _filelogger.LogError(Utilities.LogMessage(this, $"An Error Occurred Stopping Scheduled Job: {Name} - {ex}"));
                }
            }

            // stop called without start
            if (_executingTask == null)
            {
                return;
            }

            try
            {
                // force cancellation of the executing method
                _cancellationTokenSource.Cancel();
            }
            finally
            {
                // wait until the task completes or the stop token triggers
                await Task.WhenAny(_executingTask, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
            }
        }

        private DateTime RemoveSeconds(DateTime date)
        {
            return new DateTime(date.Year, date.Month, date.Day, date.Hour, date.Minute, 0, date.Kind);
        }

        private bool IsInstalled(IConfigurationRoot config)
        {
            return !string.IsNullOrEmpty(config.GetConnectionString(SettingKeys.ConnectionStringKey));
        }

        public void Dispose()
        {
            _cancellationTokenSource.Cancel();
        }
    }
}
