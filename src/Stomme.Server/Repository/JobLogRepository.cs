using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Repository
{
    public interface IJobLogRepository
    {
        Task<IEnumerable<JobLog>> GetJobLogsAsync();
        Task<IEnumerable<JobLog>> GetJobLogsAsync(int jobId);
        Task<JobLog> AddJobLogAsync(JobLog jobLog);
        Task<JobLog> UpdateJobLogAsync(JobLog jobLog);
        Task<JobLog> GetJobLogAsync(int jobLogId);
        Task DeleteJobLogAsync(int jobLogId);
    }

    /// <summary>
    /// The JobLog cluster on the persistence seam (D52) — the MASTER half of the master-bookkeeping wave.
    /// JobLog-table access goes through <see cref="IJobLogStore"/> (writes) and the MASTER read path
    /// <see cref="IMasterStoreSessionFactory"/> (reads, both JobLog AND the Job it stitches on).
    /// </summary>
    /// <remarks>
    /// <para>
    /// MASTER, not tenant, for the same reason as <see cref="JobRepository"/>: JobLog is installation-wide and
    /// the scheduler writes it from a background scope with no tenant.
    /// </para>
    /// <para>
    /// TWO seam hazards this cluster carries, both handled here rather than in the query:
    /// </para>
    /// <para>
    /// 1. THE SEVERED NAVIGATION (D45). <c>JobLog.Job</c> was a mapped EF navigation the harvested repository
    /// eager-loaded with <c>.Include(item =&gt; item.Job)</c>; it is now <c>[NotMapped]</c> view state populated
    /// here from a second master-session query, exactly as <c>SiteGroupMemberRepository</c> stitches
    /// <c>SiteGroup</c>. The Admin &gt; Jobs &gt; Log screen dereferences <c>log.Job.Name</c> and
    /// <c>log.Job.IsExecuting</c>, so a null Job would NRE the screen — the population is not optional.
    /// </para>
    /// <para>
    /// 2. THE -1 WILDCARD FOLD (D45). The harvested <c>GetJobLogsAsync</c> pushed the sentinel into the
    /// predicate: <c>Where(item =&gt; item.JobId == jobId || jobId == -1)</c>. EF folds <c>-1 == -1</c> to true
    /// and drops the arm; RavenDB's LINQ provider does NOT, and would compile the wildcard arm into RQL that
    /// matches NOTHING — returning 0 logs for the "all" call while passing on EF, the exact silent divergence
    /// D45 measured on SiteGroupMember. So the filter is composed in C# and the provider is only ever sent an
    /// arm that narrows.
    /// </para>
    /// <para>
    /// 3. WRITES resolve a FRESH store per write (<see cref="NewStore"/>), the UrlMapping per-write-store pattern
    /// (D47, BL-11). The scheduler opens a log running (<see cref="AddJobLogAsync"/>) and finalizes the SAME log
    /// (<see cref="UpdateJobLogAsync"/>) inside ONE scope every run tick. A store owns ONE <c>MasterDBContext</c>
    /// and <c>EfRepository</c> leaves each write TRACKED, so the finalizing Update through the same store attaches
    /// a second instance with the same key and EF throws <c>already being tracked</c> — which left every run's
    /// log un-finalized (Succeeded=0, FinishDate NULL). <see cref="IJobLogStore"/> is registered transient, so a
    /// fresh store per write has an empty tracker. RavenDB is unaffected (a fresh session per write).
    /// </para>
    /// </remarks>
    public class JobLogRepository : IJobLogRepository
    {
        private readonly IServiceProvider _services;
        private readonly IMasterStoreSessionFactory _sessions;

        public JobLogRepository(IServiceProvider services, IMasterStoreSessionFactory sessions)
        {
            _services = services;
            _sessions = sessions;
        }

        // A store bound to a FRESH unit of work per write (BL-11 / the D47 UrlMapping pattern). IJobLogStore is
        // registered transient, so each call yields a new store over a new MasterDBContext with an empty change
        // tracker — letting the scheduler add then finalize the same log in one scope. See hazard 3 in the remarks.
        private IJobLogStore NewStore() => _services.GetRequiredService<IJobLogStore>();

        // Reads go through a MASTER query session, never the store (D19). Materialized inside the method so no
        // IQueryable escapes (D7); plain LINQ so the read is not bound to the relational provider.
        private async Task<T> QueryAsync<T>(System.Func<IQuerySession, T> read)
        {
            var session = _sessions.OpenQuerySession();
            try
            {
                return read(session);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        public async Task<IEnumerable<JobLog>> GetJobLogsAsync()
        {
            return await GetJobLogsAsync(-1);
        }

        public async Task<IEnumerable<JobLog>> GetJobLogsAsync(int jobId)
        {
            var jobLogs = await QueryAsync(session =>
            {
                var query = session.Query<JobLogAggregate>();

                // Composed in C#: the -1 "all" case sends NO filter, so no provider ever has to fold -1 == -1.
                if (jobId != -1)
                {
                    query = query.Where(item => item.JobId == jobId);
                }

                return query.OrderByDescending(item => item.Id).ToList().ConvertAll(JobLogMapper.ToModel);
            });

            await PopulateJobsAsync(jobLogs);
            return jobLogs;
        }

        public async Task<JobLog> AddJobLogAsync(JobLog jobLog)
        {
            var aggregate = JobLogMapper.ToAggregate(jobLog, assignId: false);
            await NewStore().AddAsync(aggregate);
            JobLogMapper.CopyPersistedValues(aggregate, jobLog);
            return jobLog;
        }

        public async Task<JobLog> UpdateJobLogAsync(JobLog jobLog)
        {
            var aggregate = JobLogMapper.ToAggregate(jobLog, assignId: true);
            await NewStore().UpdateAsync(aggregate);
            JobLogMapper.CopyPersistedValues(aggregate, jobLog);
            return jobLog;
        }

        public async Task<JobLog> GetJobLogAsync(int jobLogId)
        {
            var jobLog = await QueryAsync(session => JobLogMapper.ToModel(
                session.Query<JobLogAggregate>().FirstOrDefault(item => item.Id == jobLogId)));

            if (jobLog != null)
            {
                await PopulateJobsAsync([jobLog]);
            }

            return jobLog;
        }

        public async Task DeleteJobLogAsync(int jobLogId)
        {
            // The one read through the store (D19): it deletes the very instance it loaded, so read and delete
            // share ONE store instance.
            var store = NewStore();
            var aggregate = await store.GetByIdAsync(jobLogId);
            if (aggregate == null)
            {
                return;
            }

            await store.DeleteAsync(aggregate);
        }

        // The severed .Include(item => item.Job), replayed as a second query and stitched on by hand (D45). Reads
        // only the jobs the logs actually reference (a Contains over the distinct ids), not every job — the
        // harvested .Include carried no join predicate, so no database join is lost (D22 rule 9).
        private async Task PopulateJobsAsync(IReadOnlyList<JobLog> jobLogs)
        {
            if (jobLogs.Count == 0)
            {
                return;
            }

            var jobIds = jobLogs.Select(item => item.JobId).Distinct().ToList();

            var jobsById = await QueryAsync(session => session.Query<JobAggregate>()
                .Where(item => jobIds.Contains(item.Id))
                .ToList()
                .ToDictionary(item => item.Id, JobMapper.ToModel));

            foreach (var jobLog in jobLogs)
            {
                jobLog.Job = jobsById.TryGetValue(jobLog.JobId, out var job) ? job : null;
            }
        }
    }
}
