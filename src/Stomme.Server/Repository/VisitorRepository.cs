using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;

namespace Stomme.Repository
{
    public interface IVisitorRepository
    {
        Task<IEnumerable<Visitor>> GetVisitorsAsync(int siteId, DateTime fromDate);
        Task<Visitor> AddVisitorAsync(Visitor visitor);
        Task<Visitor> UpdateVisitorAsync(Visitor visitor);
        Task<Visitor> GetVisitorAsync(int visitorId);
        Task<Visitor> GetVisitorAsync(int siteId, string IPAddress);
        Task DeleteVisitorAsync(int visitorId);
        Task<int> DeleteVisitorsAsync(int siteId, int age);
    }

    /// <summary>
    /// The Visitor cluster on the persistence seam: writes through <see cref="IVisitorStore"/>, reads
    /// through <see cref="IFrameworkStoreSessionFactory"/>. The <c>DbSet&lt;Visitor&gt;</c> is gone from
    /// <see cref="TenantDBContext"/> — that deletion is the compiler's proof this cluster moved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its reference into the User aggregate was severed, BEFORE this increment — <c>Visitor.User</c> is
    /// <c>[NotMapped]</c> view state and <see cref="GetVisitorsAsync"/> re-populates it from the User
    /// aggregate. So this increment moved the ROW and nothing else; the navigation work was already done.
    /// </para>
    /// <para>
    /// <b>ON THE RENDER PATH.</b> <c>App.razor</c>'s <c>TrackVisitorAsync</c> runs on every request to a site
    /// with <c>VisitorTracking</c> enabled and cookies allowed: it correlates by cookie or IP, INSERTS a new
    /// visitor if neither matches, and otherwise re-reads and UPDATES the existing one past its cookie expiry.
    /// Every method below except <see cref="DeleteVisitorsAsync"/> and <see cref="DeleteVisitorAsync"/> is on
    /// that path.
    /// </para>
    /// </remarks>
    public class VisitorRepository : IVisitorRepository
    {
        private readonly IVisitorStore _store;
        private readonly IFrameworkStoreSessionFactory _sessions;

        public VisitorRepository(IVisitorStore store, IFrameworkStoreSessionFactory sessions)
        {
            _store = store;
            _sessions = sessions;
        }

        /// <summary>
        /// Opens a read session, runs <paramref name="read"/> against it and disposes it. Reads NEVER go
        /// through the store — the store is the write path.
        /// </summary>
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

        public async Task<IEnumerable<Visitor>> GetVisitorsAsync(int siteId, DateTime fromDate)
        {
            // The site/date filter is on the Visitor's OWN columns and stays exactly where it was, in the
            // database. Both arms narrow, and both are plain comparisons every provider translates — there is
            // no sentinel and no constant to fold (contrast a (siteId == -1 || ...) filter, which RavenDB did not
            // fold and which returned nothing).
            var visitors = await QueryAsync(session => session.Query<VisitorAggregate>()
                .Where(item => item.SiteId == siteId && item.VisitedOn >= fromDate)
                .ToList()
                .ConvertAll(VisitorMapper.ToModel));

            // Populate the severed navigation from the User aggregate. Read through the seam's query
            // session rather than IUserRepository: one IN (...) query for the page of visitors, where the
            // repository offers only GetUsers() (every user in the tenant) or a per-visitor GetUser(id) (N+1).
            var userIds = visitors.Where(item => item.UserId != null).Select(item => item.UserId.Value).Distinct().ToList();
            if (userIds.Count != 0)
            {
                var users = await QueryAsync(session => session.Query<UserAggregate>()
                    // WHERE user_id = ANY ($1) — filtered in the database. The same Contains-over-a-key-list
                    // shape proven on both providers.
                    .Where(item => userIds.Contains(item.Id))
                    .ToList());

                foreach (var visitor in visitors.Where(item => item.UserId != null))
                {
                    // Admin > Visitors renders context.User.DisplayName guarded by `context.UserId != null`,
                    // so a visitor with a UserId MUST have a User: null here is an NRE in the admin UI, not a
                    // silent degradation. Anonymous visitors (UserId null) keep a null User, exactly as the
                    // eager-loaded navigation left them.
                    visitor.User = UserMapper.ToModel(users.Find(item => item.Id == visitor.UserId.Value));
                }
            }

            return visitors;
        }

        public async Task<Visitor> AddVisitorAsync(Visitor visitor)
        {
            var aggregate = VisitorMapper.ToAggregate(visitor, assignId: false);
            await _store.AddAsync(aggregate);
            // App.razor reads visitor.VisitorId straight into the response cookie — see VisitorMapper.
            VisitorMapper.CopyPersistedValues(aggregate, visitor);
            return visitor;
        }

        public async Task<Visitor> UpdateVisitorAsync(Visitor visitor)
        {
            var aggregate = VisitorMapper.ToAggregate(visitor, assignId: true);
            await _store.UpdateAsync(aggregate);
            VisitorMapper.CopyPersistedValues(aggregate, visitor);
            return visitor;
        }

        /// <summary>
        /// NOTE the harvested signature had no <c>tracking</c> overload and this one never needed it: the
        /// caller that mutates the result (<c>App.razor</c>) hands it straight back to
        /// <see cref="UpdateVisitorAsync"/>, which re-attaches it. Even before the seam that was a
        /// cross-context detached update — <c>FindAsync</c> tracked on one context and
        /// <c>db.Entry(...).State = Modified</c> ran on another — so nothing here ever depended on change
        /// tracking to persist, and the port is behaviour-preserving. Contrast the Language cluster, whose
        /// "one default per site" invariant DID mutate tracked entities and broke silently when ported.
        /// </summary>
        public async Task<Visitor> GetVisitorAsync(int visitorId)
        {
            return await QueryAsync(session => VisitorMapper.ToModel(session.Query<VisitorAggregate>()
                .FirstOrDefault(item => item.Id == visitorId)));
        }

        public async Task<Visitor> GetVisitorAsync(int siteId, string IPAddress)
        {
            // The IP-correlation read. Both arms narrow; an exact string equality, not a StartsWith or a
            // case-insensitive compare, so there is nothing here a provider can translate differently.
            return await QueryAsync(session => VisitorMapper.ToModel(session.Query<VisitorAggregate>()
                .FirstOrDefault(item => item.SiteId == siteId && item.IPAddress == IPAddress)));
        }

        public async Task DeleteVisitorAsync(int visitorId)
        {
            var visitor = await _store.GetByIdAsync(visitorId);
            await _store.DeleteAsync(visitor);
        }

        /// <summary>
        /// The nightly purge, run by <c>PurgeJob</c> per site.
        /// </summary>
        /// <remarks>
        /// <b>A NAMED COST REGRESSION.</b> The harvested code deleted each batch with one
        /// <c>RemoveRange</c> + <c>SaveChanges</c> — a single round trip per 100 rows. The seam's
        /// <see cref="IRepository{TAggregate, TId}"/> has no batch delete, so this issues one
        /// <see cref="IVisitorStore.DeleteAsync"/> per visitor: 100 round trips per batch instead of 1. The
        /// batching itself is KEPT (the 100-row read cap, the re-read loop) because it is what bounds memory,
        /// and the ORDER BY VisitedOn + Take(100) still runs in the database. This is a background job on a
        /// retention window, not a request path, so the extra round trips cost latency nobody waits on — but it
        /// is a real regression and it is named here rather than buried. A batch-delete primitive on the seam
        /// would remove it; that is a Phase 5 concern, not this increment's.
        /// </remarks>
        public async Task<int> DeleteVisitorsAsync(int siteId, int age)
        {
            var count = 0;
            var purgedate = DateTime.UtcNow.AddDays(-age);

            var visitors = await ReadPurgeBatchAsync(siteId, purgedate);
            while (visitors.Count > 0)
            {
                count += visitors.Count;
                foreach (var visitor in visitors)
                {
                    await _store.DeleteAsync(visitor);
                }
                visitors = await ReadPurgeBatchAsync(siteId, purgedate);
            }

            return count;
        }

        // Deliberately a session read, not a store read: the cap and the ordering must stay in the database
        // (this is the largest table in the tenant on a busy site), and the store offers only GetById/GetAll.
        private async Task<List<VisitorAggregate>> ReadPurgeBatchAsync(int siteId, DateTime purgedate) =>
            await QueryAsync(session => session.Query<VisitorAggregate>()
                .Where(item => item.SiteId == siteId && item.VisitedOn < purgedate)
                .OrderBy(item => item.VisitedOn)
                .Take(100)
                .ToList());
    }
}
