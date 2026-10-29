using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Repository
{
    public interface IJobRepository
    {
        Task<IEnumerable<Job>> GetJobsAsync();
        Task<Job> AddJobAsync(Job job);
        Task<Job> UpdateJobAsync(Job job);
        Task<Job> GetJobAsync(string jobType);
        Task<Job> GetJobAsync(int jobId);
        Task<Job> GetJobAsync(int jobId, bool tracking);
        Task DeleteJobAsync(int jobId);
    }

    /// <summary>
    /// The Job cluster on the persistence seam — the MASTER half of the master-bookkeeping wave. Job-table
    /// access goes through <see cref="IJobStore"/> (writes) and the MASTER read path
    /// <see cref="IMasterStoreSessionFactory"/> (reads); this class holds no <c>DbContext</c> and no
    /// <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MASTER, not tenant (D23's master half): Job is installation-wide, so reads go through
    /// <see cref="IMasterStoreSessionFactory"/> — NOT the tenant <see cref="IFrameworkStoreSessionFactory"/> —
    /// and writes through a store bound to <see cref="MasterDBContext"/>. The scheduler
    /// (<c>HostedServiceBase</c>) calls this from a background scope with no request and no tenant, which the
    /// master session factory serves because it never reads <c>SiteState.Alias</c>.
    /// </para>
    /// <para>
    /// Reads NEVER go through the store: the store TRACKS what it loads, and a later Update attaching a
    /// DIFFERENT instance with the same key is rejected by EF. A query session opens its own context, so reads
    /// leave the store's change tracker clean. The one read through the store is <see cref="DeleteJobAsync"/>,
    /// which deletes the very instance it loaded — tracking is what we want there.
    /// </para>
    /// <para>
    /// WRITES resolve a FRESH store per write (<see cref="NewStore"/>), NOT a constructor-injected one — the
    /// UrlMapping per-write-store pattern. The scheduler (<c>HostedServiceBase</c>) writes the SAME
    /// job row twice inside ONE scope every tick (auto-register then flip <c>IsExecuting</c>; or flip executing
    /// then advance <c>NextExecution</c>). A store owns ONE <c>MasterDBContext</c> and <c>EfRepository</c> leaves
    /// each write TRACKED, so a second write through the same store attaches a second instance with the same key
    /// and EF throws <c>InvalidOperationException: ... already being tracked</c>. <see cref="IJobStore"/> is
    /// registered transient, so each <see cref="NewStore"/> is a fresh store over a fresh context — restoring the
    /// per-method-context lifetime the harvested code had. RavenDB is unaffected: its store opens a fresh session
    /// per write, so no identity map outlives a write there.
    /// </para>
    /// <para>
    /// PROVIDER-EQUALITY NOTE. <see cref="GetJobAsync(string)"/> filters <c>JobType == jobType</c> with a
    /// bare <c>==</c>, which is case-SENSITIVE on the relational providers and case-INSENSITIVE on RavenDB. It is
    /// deliberately NOT normalized: <see cref="Job.JobType"/> is a fully-qualified .NET type name that is fed
    /// straight to <c>Type.GetType</c>, whose CASE is load-bearing, and two job types never differ by case
    /// alone. Normalizing the column would break <c>Type.GetType</c>; the honest divergence is inert here.
    /// </para>
    /// </remarks>
    public class JobRepository : IJobRepository
    {
        private readonly IServiceProvider _services;
        private readonly IMasterStoreSessionFactory _sessions;
        private readonly ICacheManager _cache;

        public JobRepository(IServiceProvider services, IMasterStoreSessionFactory sessions, ICacheManager cache)
        {
            _services = services;
            _sessions = sessions;
            _cache = cache;
        }

        // A store bound to a FRESH unit of work per write (BL-11 / the D47 UrlMapping pattern). IJobStore is
        // registered transient, so every call yields a new store over a new MasterDBContext with an empty change
        // tracker; the scope disposes them. This is what lets the scheduler write the same job row twice in one
        // scope without hitting the identity map. See the class remarks.
        private IJobStore NewStore() => _services.GetRequiredService<IJobStore>();

        // The read path on the seam: reads go through a MASTER query session, never the store, so the
        // store's tracker stays clean for the Get-then-Update flows the scheduler runs (GetJobAsync(jobType)
        // then UpdateJobAsync). The IQueryable is materialized inside this method, so it never leaves the
        // repository. Plain LINQ rather than EF's async operators, which would bind these reads to the
        // relational provider.
        private async Task<T> QueryAsync<T>(Func<IQuerySession, T> read)
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

        public async Task<IEnumerable<Job>> GetJobsAsync()
        {
            // Remove any jobs whose type is no longer loaded (an uninstalled module). Read the jobs through the
            // session (untracked), delete the missing ones through the store, then re-read through a fresh
            // session so the returned set reflects the deletions — the harvested code read _db.Job twice for
            // the same reason.
            foreach (var aggregate in await QueryAsync(session => session.Query<JobAggregate>().ToList()))
            {
                if (Type.GetType(aggregate.JobType) == null)
                {
                    await DeleteJobAsync(aggregate.Id);
                }
            }

            return await QueryAsync(session => session.Query<JobAggregate>().ToList().ConvertAll(JobMapper.ToModel));
        }

        public async Task<Job> AddJobAsync(Job job)
        {
            var aggregate = JobMapper.ToAggregate(job, assignId: false);
            await NewStore().AddAsync(aggregate);
            JobMapper.CopyPersistedValues(aggregate, job);
            // Invalidate the negative cache that GetJobAsync(string) writes for a miss (D19 read path caches
            // null under Job:{jobType}). Without this, the scheduler's auto-registration reads a stale null on
            // every subsequent tick and re-INSERTs an existing JobType — UNIQUE constraint failed: Job.JobType,
            // forever. UpdateJobAsync/DeleteJobAsync already invalidate this key; AddJobAsync must too.
            await _cache.RemoveCacheAsync($"Job:{job.JobType}");
            return job;
        }

        public async Task<Job> UpdateJobAsync(Job job)
        {
            var aggregate = JobMapper.ToAggregate(job, assignId: true);
            await NewStore().UpdateAsync(aggregate);
            JobMapper.CopyPersistedValues(aggregate, job);
            await _cache.RemoveCacheAsync($"Job:{job.JobType}");
            return job;
        }

        public async Task<Job> GetJobAsync(string jobType)
        {
            return await _cache.GetCacheAsync($"Job:{jobType}", async entry =>
            {
                return await QueryAsync(session => JobMapper.ToModel(
                    session.Query<JobAggregate>().FirstOrDefault(item => item.JobType == jobType)));
            });
        }

        public async Task<Job> GetJobAsync(int jobId)
        {
            return await QueryAsync(session => JobMapper.ToModel(
                session.Query<JobAggregate>().FirstOrDefault(item => item.Id == jobId)));
        }

        public async Task<Job> GetJobAsync(int jobId, bool tracking)
        {
            // The `tracking` flag is moot on the seam: a query-session read is ALWAYS detached, so both
            // branches return a detached Job. The harvested tracking read exists only for JobController.Put's
            // existence check, which discards the entity. Kept in the signature for source compatibility.
            return await GetJobAsync(jobId);
        }

        public async Task DeleteJobAsync(int jobId)
        {
            // The one read that legitimately goes through the store: it deletes the very instance it
            // loaded, so tracking is what we want and no second instance is ever attached. Read and delete share
            // ONE store instance so the delete targets the tracked entity it loaded.
            var store = NewStore();
            var aggregate = await store.GetByIdAsync(jobId);
            if (aggregate == null)
            {
                // The harvested code threw NullReferenceException for an unknown id. Returning silently matches
                // DeletePermission/DeleteSetting's shape — a deliberate, strictly-safer divergence.
                return;
            }

            var jobType = aggregate.JobType;
            await store.DeleteAsync(aggregate);
            await _cache.RemoveCacheAsync($"Job:{jobType}");
        }
    }
}
