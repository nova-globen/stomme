using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Persistence.Abstractions;
using Stomme.Repository.Stores;

namespace Stomme.Repository
{
    public interface INotificationRepository
    {
        Task<IEnumerable<Notification>> GetNotificationsAsync(int siteId, int fromUserId, int toUserId);
        Task<IEnumerable<Notification>> GetNotificationsAsync(int siteId, int fromUserId, int toUserId, int count, bool isRead);
        Task<int> GetNotificationCountAsync(int siteId, int fromUserId, int toUserId, bool isRead);
        Task<Notification> AddNotificationAsync(Notification notification);
        Task<Notification> UpdateNotificationAsync(Notification notification);
        Task<Notification> GetNotificationAsync(int notificationId);
        Task<Notification> GetNotificationAsync(int notificationId, bool tracking);
        Task DeleteNotificationAsync(int notificationId);
        Task<int> DeleteNotificationsAsync(int siteId, int age);
    }

    /// <summary>
    /// The Notification cluster on the persistence seam. Notification-table access goes through
    /// <see cref="INotificationStore"/> (writes) and <see cref="IQuerySession.Query{T}"/> (reads); this class
    /// holds no <c>DbContext</c> and no <c>IQueryable</c> escapes it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A LEAF cluster: no repository edges in either direction, no navigations, one table, tenant-side only.
    /// <see cref="NotificationAggregate"/> is the first <c>IDeletable</c> aggregate on the seam — see the
    /// remarks there for why that interface is load-bearing and for the provider divergence it inherits.
    /// </para>
    /// <para>
    /// The <c>-1</c> sentinel on <c>fromUserId</c>/<c>toUserId</c> means "any", and the <c>(-1, -1)</c> pair
    /// selects a completely DIFFERENT query — the delivery queue (undelivered, not deleted, due) rather than a
    /// user's mailbox. That is why every read below branches on it rather than composing: the two branches
    /// share no predicate but SiteId.
    /// </para>
    /// </remarks>
    public class NotificationRepository : INotificationRepository
    {
        private readonly INotificationStore _store;
        private readonly IFrameworkStoreSessionFactory _sessions;

        public NotificationRepository(INotificationStore store, IFrameworkStoreSessionFactory sessions)
        {
            _store = store;
            _sessions = sessions;
        }

        // The read path on the seam: reads NEVER go through the store, because the store TRACKS what it
        // loads and UpdateNotification would then attach a DIFFERENT instance with the same key, which EF
        // rejects. NotificationController.Put and NotificationJob both walk straight into it — read, then write
        // the same row back, on ONE repository instance. A query session opens its own context, so reads leave
        // the store's tracker clean.
        //
        // The IQueryable is materialized inside this method, so it never leaves the repository. The LINQ
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

        /// <summary>
        /// The DELIVERY QUEUE predicate — what <c>NotificationJob</c> asks for: undelivered, not deleted, and
        /// due to send. Selected by the <c>(-1, -1)</c> sentinel pair, and shared by all three reads so the
        /// three cannot drift apart.
        /// </summary>
        private static IQueryable<NotificationAggregate> QueueOf(IQuerySession session, int siteId) =>
            session.Query<NotificationAggregate>()
                .Where(item => item.SiteId == siteId)
                .Where(item => item.IsDelivered == false && item.IsDeleted == false)
                .Where(item => item.SendOn == null || item.SendOn < DateTime.UtcNow);

        /// <summary>
        /// The MAILBOX predicate — a user's sent or received notifications, with <c>-1</c> meaning "any".
        /// Note it deliberately does NOT filter IsDeleted: the harvested mailbox read returns soft-deleted
        /// notifications and the controller decides what to show.
        /// </summary>
        /// <remarks>
        /// The <c>-1</c> sentinels are branched HERE, in C#, not folded into the predicate as the harvested
        /// <c>item.ToUserId == toUserId || toUserId == -1</c> did. EF folds the constant comparison; RavenDB does NOT
        /// (the D45 class, fixed the same way in <see cref="JobLogRepository"/>) - MEASURED 2026-09-14 (BL-78 step 3,
        /// <c>RavenNotificationChannelSeamConformanceTests</c>): on an installed RavenDB host the mailbox read
        /// <c>(siteId, -1, userId)</c> returned ZERO rows for a user with an inbox row that SQLite and PostgreSQL
        /// returned, i.e. every user's inbox read empty on RavenDB. Same rows on EF; a composed filter per real id.
        /// </remarks>
        private static IQueryable<NotificationAggregate> MailboxOf(IQuerySession session, int siteId, int fromUserId, int toUserId)
        {
            var query = session.Query<NotificationAggregate>().Where(item => item.SiteId == siteId);
            if (toUserId != -1)
            {
                query = query.Where(item => item.ToUserId == toUserId);
            }

            if (fromUserId != -1)
            {
                query = query.Where(item => item.FromUserId == fromUserId);
            }

            return query;
        }

        public async Task<IEnumerable<Notification>> GetNotificationsAsync(int siteId, int fromUserId, int toUserId)
        {
            return await QueryAsync(session => (toUserId == -1 && fromUserId == -1
                    ? QueueOf(session, siteId)
                    : MailboxOf(session, siteId, fromUserId, toUserId))
                .ToList()
                .ConvertAll(NotificationMapper.ToModel));
        }

        public async Task<IEnumerable<Notification>> GetNotificationsAsync(int siteId, int fromUserId, int toUserId, int count, bool isRead)
        {
            // NOTE THE TAKE IS IN MEMORY, AND THAT IS HARVESTED BEHAVIOUR, NOT AN OVERSIGHT TO FIX HERE.
            // The harvested code materialized with ToListAsync() and only then applied .Take(count) — so the
            // database returns every matching notification and the cap is applied to the list. Preserved
            // exactly: pushing the Take into the database would change the query this cluster issues, which is
            // a different argument (and a defensible one) belonging to whoever owns this read's performance.
            // The filters and the ORDER BY do stay in the database, as they did.
            return await QueryAsync(session => (toUserId == -1 && fromUserId == -1
                    ? QueueOf(session, siteId)
                    : MailboxOf(session, siteId, fromUserId, toUserId))
                .Where(item => item.IsRead == isRead)
                .OrderByDescending(item => item.CreatedOn)
                .ToList()
                .Take(count)
                .Select(NotificationMapper.ToModel));
        }

        public async Task<int> GetNotificationCountAsync(int siteId, int fromUserId, int toUserId, bool isRead)
        {
            // NOTE THE COUNT IS IN MEMORY, AND THAT IS HARVESTED BEHAVIOUR, NOT AN OVERSIGHT TO FIX HERE.
            // The harvested code did ToListAsync() and then .Count() — materializing every matching row to
            // count it, where the database could have returned the number. Preserved exactly; "fix" it and
            // this stops being a storage move. It is named rather than hidden: this read runs on the
            // notification badge, so it is the one in-memory operation in this batch that a busy mailbox
            // actually pays for. See src/Stomme.Server/README.md (persistence seam).
            return await QueryAsync(session => (toUserId == -1 && fromUserId == -1
                    ? QueueOf(session, siteId)
                    : MailboxOf(session, siteId, fromUserId, toUserId))
                .Where(item => item.IsRead == isRead)
                .ToList()
                .Count);
        }

        public async Task<Notification> AddNotificationAsync(Notification notification)
        {
            var aggregate = NotificationMapper.ToAggregate(notification, assignId: false);
            await _store.AddAsync(aggregate);
            NotificationMapper.CopyPersistedValues(aggregate, notification);
            return notification;
        }

        public async Task<Notification> UpdateNotificationAsync(Notification notification)
        {
            var aggregate = NotificationMapper.ToAggregate(notification, assignId: true);
            await _store.UpdateAsync(aggregate);
            // Copies back the IDeletable stamps DbContextUtils applies when a notification is soft-deleted —
            // see NotificationMapper.CopyPersistedValues.
            NotificationMapper.CopyPersistedValues(aggregate, notification);
            return notification;
        }

        public async Task<Notification> GetNotificationAsync(int notificationId)
        {
            return await GetNotificationAsync(notificationId, true);
        }

        /// <summary>
        /// Reads one notification. <paramref name="tracking"/> is now INERT — see the remarks.
        /// </summary>
        /// <remarks>
        /// The parameter is kept and deliberately ignored, exactly as on
        /// <see cref="ProfileRepository.GetProfileAsync(int, bool)"/>: <c>tracking: false</c> was
        /// <c>NotificationController.Put</c>'s workaround for the very D19 identity conflict this seam removes
        /// structurally, and every read here now goes through a query session with its own context, so nothing
        /// returned is ever tracked. It stays on <see cref="INotificationRepository"/> because that is public
        /// API downstream apps bind to; retiring it is a breaking change and not a storage move's business.
        /// </remarks>
        public async Task<Notification> GetNotificationAsync(int notificationId, bool tracking)
        {
            return await QueryAsync(session => NotificationMapper.ToModel(session.Query<NotificationAggregate>()
                .FirstOrDefault(item => item.Id == notificationId)));
        }

        public async Task DeleteNotificationAsync(int notificationId)
        {
            // The one read that legitimately goes through the store: it deletes the very instance it
            // loaded, so tracking is what we want and no second instance is ever attached.
            //
            // A HARD delete, despite the aggregate being IDeletable. Harvested and unchanged: the soft delete
            // is expressed by callers setting IsDeleted and calling UpdateNotification; this method removes the
            // row.
            var aggregate = await _store.GetByIdAsync(notificationId);
            if (aggregate == null)
            {
                // The harvested code threw NullReferenceException here. Returning silently for an unknown id
                // matches the shape every migrated cluster settled on — a deliberate, strictly-safer
                // divergence.
                return;
            }

            await _store.DeleteAsync(aggregate);
        }

        public async Task<int> DeleteNotificationsAsync(int siteId, int age)
        {
            // delete notifications in batches of 100 records
            //
            // COST REGRESSION, accepted: the harvested code did RemoveRange + ONE SaveChanges per batch
            // of 100; the seam has no bulk delete, so this is a filtered session read plus one round trip PER
            // ROW. Parity in effect, not in cost. Nightly, off the render path, and PurgeJob wraps the call in
            // its own try/catch. See src/Stomme.Server/README.md (persistence seam).
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
        /// One batch of purgeable notifications, filtered and ordered and capped in the DATABASE. Re-read each
        /// pass, as the harvested loop did.
        /// </summary>
        /// <remarks>
        /// The predicate is the harvested one and its precedence is worth reading twice:
        /// <c>FromUserId == null &amp;&amp; (IsDeleted || IsDelivered &amp;&amp; DeliveredOn &lt; purgedate)</c>.
        /// <c>&amp;&amp;</c> binds tighter than <c>||</c>, so it purges SYSTEM notifications (FromUserId null)
        /// that are either soft-deleted OR delivered-and-old. The FromUserId arm is what keeps user-to-user
        /// notifications out of the purge entirely, and the parentheses are what stop a soft-deleted
        /// notification from also having to be old. Reproduced verbatim.
        /// </remarks>
        private async Task<List<NotificationAggregate>> ReadPurgeBatchAsync(int siteId, DateTime purgedate) =>
            await QueryAsync(session => session.Query<NotificationAggregate>()
                .Where(item => item.SiteId == siteId && item.FromUserId == null
                    && (item.IsDeleted || item.IsDelivered && item.DeliveredOn < purgedate))
                .OrderBy(item => item.DeliveredOn)
                .Take(100)
                .ToList());
    }
}
