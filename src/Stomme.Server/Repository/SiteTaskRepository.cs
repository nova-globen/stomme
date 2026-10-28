using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;

namespace Stomme.Repository
{
    public interface ISiteTaskRepository
    {
        Task<IEnumerable<SiteTask>> GetSiteTasksAsync(int siteId);
        Task<SiteTask> GetSiteTaskAsync(int siteTaskId);
        Task<SiteTask> AddSiteTaskAsync(SiteTask siteTask);
        Task<SiteTask> UpdateSiteTaskAsync(SiteTask siteTask);
        Task DeleteSiteTaskAsync(int siteTaskId);
        Task<int> DeleteSiteTasksAsync(int siteId, int age);
    }

    /// <summary>
    /// The SiteTask cluster on the persistence seam (D3/D7/D44). SiteTask-table access goes through
    /// <see cref="ISiteTaskStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads); this class holds
    /// no <c>DbContext</c> and no <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A LEAF cluster: no repository edges in either direction, no navigations, one table, tenant-side only.
    /// </para>
    /// <para>
    /// Read and written by <c>SiteTaskJob</c>, which runs once per tenant and calls <c>SetTenant</c> for each
    /// (<c>HostedServiceBase</c>). It resolves this repository once per tenant, AFTER that call, which is what
    /// makes a per-instance context tenant-correct — and is why this repository and its store must stay
    /// TRANSIENT. See <c>EfFrameworkStore&lt;TAggregate&gt;</c>. (This used to say the job runs every tenant
    /// inside ONE DI scope; since BL-107 / <c>ff19714</c>, 2026-09-17, each tenant gets its own tenant scope and
    /// its own DI scope. TRANSIENT is still required: it is what stops a cached context binding to one tenant.)
    /// </para>
    /// </remarks>
    public class SiteTaskRepository : ISiteTaskRepository
    {
        private readonly ISiteTaskStore _store;
        private readonly IFrameworkStoreSessionFactory _sessions;

        public SiteTaskRepository(ISiteTaskStore store, IFrameworkStoreSessionFactory sessions)
        {
            _store = store;
            _sessions = sessions;
        }

        // The read path on the seam (D19): reads NEVER go through the store, because the store TRACKS what it
        // loads and UpdateSiteTask would then attach a DIFFERENT instance with the same key, which EF rejects.
        // SiteTaskJob walks straight into it — it reads the pending tasks, runs each and writes it back
        // completed, on ONE repository instance. A query session opens its own context, so reads leave the
        // store's tracker clean.
        //
        // The IQueryable is materialized inside this method, so it never leaves the repository (D7). The LINQ
        // operators are plain LINQ rather than EF Core's async ones, which would bind these reads to the
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

        public async Task<IEnumerable<SiteTask>> GetSiteTasksAsync(int siteId)
        {
            // The work queue: PENDING tasks only, oldest first. Both arms stay in the DATABASE (rule 9). The
            // !IsCompleted arm is the discriminator — lose it and SiteTaskJob re-runs every task it has ever
            // completed, which for a table whose Type column is reflected over and executed is not a subtle
            // failure.
            return await QueryAsync(session => session.Query<SiteTaskAggregate>()
                .Where(item => item.SiteId == siteId && !item.IsCompleted)
                .OrderBy(item => item.CreatedOn)
                .ToList()
                .ConvertAll(SiteTaskMapper.ToModel));
        }

        public async Task<SiteTask> GetSiteTaskAsync(int siteTaskId)
        {
            // SingleOrDefault on the harvested code, which throws if the key is somehow duplicated. Preserved
            // rather than relaxed to FirstOrDefault: on a primary key the two are equivalent, and if they ever
            // stop being equivalent the throw is the honest outcome.
            return await QueryAsync(session => SiteTaskMapper.ToModel(session.Query<SiteTaskAggregate>()
                .SingleOrDefault(item => item.Id == siteTaskId)));
        }

        public async Task<SiteTask> AddSiteTaskAsync(SiteTask siteTask)
        {
            var aggregate = SiteTaskMapper.ToAggregate(siteTask, assignId: false);
            await _store.AddAsync(aggregate);
            SiteTaskMapper.CopyPersistedValues(aggregate, siteTask);
            return siteTask;
        }

        public async Task<SiteTask> UpdateSiteTaskAsync(SiteTask siteTask)
        {
            var aggregate = SiteTaskMapper.ToAggregate(siteTask, assignId: true);
            await _store.UpdateAsync(aggregate);
            SiteTaskMapper.CopyPersistedValues(aggregate, siteTask);
            return siteTask;
        }

        public async Task DeleteSiteTaskAsync(int siteTaskId)
        {
            // The one read that legitimately goes through the store (D19): it deletes the very instance it
            // loaded, so tracking is what we want and no second instance is ever attached.
            var aggregate = await _store.GetByIdAsync(siteTaskId);
            if (aggregate == null)
            {
                // The harvested code threw NullReferenceException here. Returning silently for an unknown id
                // matches the shape every migrated cluster settled on — a deliberate, strictly-safer
                // divergence.
                return;
            }

            await _store.DeleteAsync(aggregate);
        }

        public async Task<int> DeleteSiteTasksAsync(int siteId, int age)
        {
            // delete completed tasks in batches of 100 records
            //
            // COST REGRESSION, accepted (D24): the harvested code did RemoveRange + ONE SaveChanges per batch
            // of 100; the seam has no bulk delete, so this is a filtered session read plus one round trip PER
            // ROW. Parity in effect, not in cost. Nightly, off the render path, and PurgeJob wraps the call in
            // its own try/catch. See src/Stomme.Server/README.md (persistence seam).
            //
            // The batching is KEPT rather than collapsed to one read: it bounds the memory this job holds.
            var count = 0;
            var purgedate = DateTime.UtcNow.AddDays(-age);

            var aggregates = await ReadPurgeBatchAsync(siteId, purgedate);
            while (aggregates.Count > 0)
            {
                foreach (var aggregate in aggregates)
                {
                    await _store.DeleteAsync(aggregate);
                    // Counted per row actually deleted — see the note in LogRepository.DeleteLogsAsync.
                    count++;
                }

                aggregates = await ReadPurgeBatchAsync(siteId, purgedate);
            }

            return count;
        }

        /// <summary>
        /// One batch of purgeable tasks — COMPLETED ones only, oldest first, capped in the DATABASE. Re-read
        /// each pass, as the harvested loop did: the previous pass's deletes are committed, so the next read
        /// cannot see them and the loop terminates.
        /// </summary>
        private async Task<List<SiteTaskAggregate>> ReadPurgeBatchAsync(int siteId, DateTime purgedate) =>
            await QueryAsync(session => session.Query<SiteTaskAggregate>()
                .Where(item => item.SiteId == siteId && item.IsCompleted && item.CreatedOn < purgedate)
                .OrderBy(item => item.CreatedOn)
                .Take(100)
                .ToList());
    }
}
