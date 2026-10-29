using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;

namespace Stomme.Repository
{
    public interface ILogRepository
    {
        Task<IEnumerable<Log>> GetLogsAsync(int siteId, string level, string function, int rows);

        /// <summary>
        /// One page of a site's log inside a half-open UTC range, in <c>Id</c> order, starting AFTER <paramref name="afterId"/>.
        /// </summary>
        Task<IReadOnlyList<Log>> GetLogsPageAsync(int siteId, DateTime fromUtc, DateTime toUtc, string exactLevel, int afterId, int take);
        Task<Log> GetLogAsync(int logId);
        Task AddLogAsync(Log log);
        Task<int> DeleteLogsAsync(int siteId, int age);
    }

    /// <summary>
    /// The Log cluster on the persistence seam. Log-table access goes through
    /// <see cref="ILogStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads); this class holds no
    /// <c>DbContext</c> and no <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A LEAF cluster: no repository edges in either direction, no navigations, one table, tenant-side only.
    /// Append-and-purge — there is no update path at all, so the tracked-instance identity conflict has no shape to take
    /// here. <see cref="LogAggregate"/> carries no audit columns; see the remarks there.
    /// </para>
    /// <para>
    /// <b>Writes arrive on a background thread, not the request thread.</b> <c>LogManager</c> enriches
    /// the entry on the caller's thread and queues it; <c>LogDrainService</c> then creates a fresh DI scope per
    /// entry, points its <c>ITenantManager</c> at the entry's captured alias and only THEN resolves this
    /// repository. That ordering is what makes a per-instance context tenant-correct here, and it is why this
    /// repository — and its store — must stay TRANSIENT. See <c>EfFrameworkStore&lt;TAggregate&gt;</c>.
    /// </para>
    /// </remarks>
    public class LogRepository : ILogRepository
    {
        private readonly ILogStore _store;
        private readonly IFrameworkStoreSessionFactory _sessions;

        public LogRepository(ILogStore store, IFrameworkStoreSessionFactory sessions)
        {
            _store = store;
            _sessions = sessions;
        }

        // The read path on the seam. The IQueryable is materialized inside this method, so it never
        // leaves the repository; the LINQ operators are plain LINQ rather than EF Core's async ones,
        // which would bind these reads to the relational provider.
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

        public async Task<IEnumerable<Log>> GetLogsAsync(int siteId, string level, string function, int rows)
        {
            // The admin event-log viewer's only read. Every arm stays in the DATABASE (rule 9): the filters,
            // the ORDER BY and — critically — the Take. This table is the largest in the tenant by row count
            // and the whole point of the `rows` cap is to not materialize it; an in-memory Take here would
            // load every log entry for the site on every page of the viewer.
            //
            // COMPOSED, where the harvested code wrote the same four predicates out as four explicit
            // branches. The predicates are unchanged — (SiteId), (SiteId, Function), (SiteId, Level),
            // (SiteId, Level, Function) — and each is still built before materialization, so every branch
            // issues the query it issued before. What is NOT done here is the tempting rewrite to a single
            // `item.Level == level || level == null` predicate: that pushes the null check into the database
            // and changes the SQL for every branch. Composing the Where clauses in C# keeps the SQL and drops
            // only the duplication. Pinned per branch by LogStoreBehaviourTests.
            //
            // Note the ordering: Take must be applied AFTER OrderByDescending, or the cap picks an arbitrary
            // `rows` entries and then sorts those — the newest entries would silently not be the ones shown.
            return await QueryAsync(session =>
            {
                var query = session.Query<LogAggregate>().Where(item => item.SiteId == siteId);

                if (level != null)
                {
                    query = query.Where(item => item.Level == level);
                }

                if (function != null)
                {
                    query = query.Where(item => item.Function == function);
                }

                return query
                    .OrderByDescending(item => item.LogDate)
                    .Take(rows)
                    .ToList()
                    .ConvertAll(LogMapper.ToModel);
            });
        }

        public async Task<IReadOnlyList<Log>> GetLogsPageAsync(int siteId, DateTime fromUtc, DateTime toUtc, string exactLevel, int afterId, int take)
        {
            // The EXPORT's read. GetLogsAsync above is "the newest N" and cannot walk a
            // range; this one can, a page at a time, without ever materialising the table.
            //
            // KEYSET ON Id, NOT LogDate. LogDate is not unique - a burst of entries shares an instant - so a cursor
            // on it either repeats the entries that straddle a page boundary (>=) or silently drops them (>). Id is
            // unique on every provider: an identity column on the relational ones, a HiLo-assigned NUMERIC field on
            // RavenDB (FindIdentityProperty is off for this seam, so Id is an ordinary stored number and `>` on it is
            // a numeric range, never a lexicographic comparison of document ids). The order is therefore Id order,
            // which is insertion order on a relational store and approximately so on RavenDB; an export is a set, and
            // what it must not do is lose or duplicate a row.
            //
            // The level arrives ALREADY CANONICAL - the exact stored spelling, from a closed set - because a bare
            // string == is case-sensitive on EF and case-insensitive on RavenDB. This method does not try to
            // repair a free-text level; the caller is not allowed to have one.
            //
            // Every arm stays in the DATABASE, as above, and the predicates are composed in C# for the same reason.
            if (take <= 0)
            {
                return Array.Empty<Log>();
            }

            return await QueryAsync(session =>
            {
                var query = session.Query<LogAggregate>()
                    .Where(item => item.SiteId == siteId)
                    .Where(item => item.LogDate >= fromUtc && item.LogDate < toUtc)
                    .Where(item => item.Id > afterId);

                if (exactLevel != null)
                {
                    query = query.Where(item => item.Level == exactLevel);
                }

                return query
                    .OrderBy(item => item.Id)
                    .Take(take)
                    .ToList()
                    .ConvertAll(LogMapper.ToModel);
            });
        }

        public async Task<Log> GetLogAsync(int logId)
        {
            return await QueryAsync(session => LogMapper.ToModel(session.Query<LogAggregate>()
                .FirstOrDefault(item => item.Id == logId)));
        }

        public async Task AddLogAsync(Log log)
        {
            // The truncations are the harvested ones and they are load-bearing, not defensive noise: each
            // matches a column width, and an over-long value is a hard failure on every provider that enforces
            // one. Unchanged, and still performed on the caller's object before mapping.
            if (log.Url.Length > 2048) log.Url = log.Url.Substring(0, 2048);
            if (log.Server.Length > 200) log.Server = log.Server.Substring(0, 200);
            if (log.Category.Length > 200) log.Category = log.Category.Substring(0, 200);
            if (log.Feature.Length > 200) log.Feature = log.Feature.Substring(0, 200);
            if (log.Function.Length > 20) log.Function = log.Function.Substring(0, 20);
            if (log.Level.Length > 20) log.Level = log.Level.Substring(0, 20);

            var aggregate = LogMapper.ToAggregate(log, assignId: false);
            await _store.AddAsync(aggregate);
            LogMapper.CopyPersistedValues(aggregate, log);
        }

        public async Task<int> DeleteLogsAsync(int siteId, int age)
        {
            // delete logs in batches of 100 records
            //
            // COST REGRESSION, accepted. The harvested code did RemoveRange + ONE SaveChanges per batch
            // of 100; the seam has no bulk delete, so this is now a filtered session read plus one round trip
            // PER ROW — 100 deletes where there was 1. Parity in effect, not in cost. It matters more here
            // than in any other cluster because Log is the biggest table in the tenant and a neglected
            // installation can purge tens of thousands of rows in a single nightly PurgeJob run. It is still
            // the nightly job, off the render path, and PurgeJob already wraps this call in its own try/catch.
            // See src/Stomme.Server/README.md (persistence seam).
            //
            // The batching is KEPT rather than collapsed to one big read: it is what bounds the memory this
            // job holds, which was its purpose before the seam and still is.
            var count = 0;
            var purgedate = DateTime.UtcNow.AddDays(-age);

            var aggregates = await ReadPurgeBatchAsync(siteId, purgedate);
            while (aggregates.Count > 0)
            {
                foreach (var aggregate in aggregates)
                {
                    await _store.DeleteAsync(aggregate);
                    // Counted per row actually deleted, not per batch read. The harvested code added the whole
                    // batch's size before deleting it, which one SaveChanges made equivalent; with a round trip
                    // per row it no longer is, and a throw part-way would otherwise report rows it never
                    // deleted.
                    count++;
                }

                aggregates = await ReadPurgeBatchAsync(siteId, purgedate);
            }

            return count;
        }

        /// <summary>
        /// One batch of purgeable rows, filtered and ordered and capped in the DATABASE. Re-read each pass, as
        /// the harvested loop did: the previous pass's deletes are committed, so the next read cannot see them
        /// and the loop terminates.
        /// </summary>
        private async Task<List<LogAggregate>> ReadPurgeBatchAsync(int siteId, DateTime purgedate) =>
            await QueryAsync(session => session.Query<LogAggregate>()
                .Where(item => item.SiteId == siteId && item.LogDate < purgedate)
                .OrderBy(item => item.LogDate)
                .Take(100)
                .ToList());
    }
}
