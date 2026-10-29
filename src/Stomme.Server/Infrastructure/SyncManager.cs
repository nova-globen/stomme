using Stomme.Models;
using Stomme.Shared;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// The framework's sync seam. It has TWO consumers with different jobs:
    /// <list type="bullet">
    ///   <item>
    ///     <see cref="Subscribe"/> — the PUSH side. Subscribers are awaited in subscription order by
    ///     <see cref="AddSyncEventAsync(int, int, string, int, string)"/>, so a subscriber that invalidates
    ///     cache completes BEFORE the write's caller proceeds.
    ///   </item>
    ///   <item>
    ///     <see cref="GetSyncEvents"/> — the POLL side. Clients poll it for PageState management. It reads an
    ///     in-memory list and touches no store, so it stays synchronous.
    ///   </item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// WHY THIS IS NOT AN EVENT. This was <c>event EventHandler&lt;SyncEvent&gt; EntityChanged</c>. A sync
    /// event cannot be awaited, so once cache invalidation had to call the async-only
    /// <see cref="ICacheManager.RemoveCacheAsync(string)"/> the only way to raise it from an event
    /// handler was <c>async void</c> — fire-and-forget. The invalidation could then land AFTER a subsequent
    /// read had already repopulated the cache from the pre-write state, leaving a stale entry with no further
    /// event to clear it. The invocation list is awaited instead, which puts invalidation back in front of the
    /// caller's next statement.
    /// </remarks>
    public interface ISyncManager
    {
        /// <summary>Registers a subscriber to be awaited on every sync event, in subscription order.</summary>
        void Subscribe(Func<SyncEvent, Task> subscriber);

        /// <summary>Removes a subscriber registered by <see cref="Subscribe"/>.</summary>
        void Unsubscribe(Func<SyncEvent, Task> subscriber);

        List<SyncEvent> GetSyncEvents(int tenantId, DateTime lastSyncDate);
        Task AddSyncEventAsync(Alias alias, string entityName, int entityId, string action);
        Task AddSyncEventAsync(int tenantId, int siteId, string entityName, int entityId, string action);

        [Obsolete("AddSyncEvent(int tenantId, string entityName, int entityId, string action) is deprecated. Use AddSyncEvent(Alias alias, string entityName, int entityId, string action) instead.", false)]
        Task AddSyncEventAsync(int tenantId, string entityName, int entityId, string action);
    }

    public class SyncManager : ISyncManager
    {
        private List<SyncEvent> SyncEvents { get; set; }

        private readonly List<Func<SyncEvent, Task>> _subscribers = new List<Func<SyncEvent, Task>>();

        public SyncManager()
        {
            SyncEvents = new List<SyncEvent>();
        }

        public void Subscribe(Func<SyncEvent, Task> subscriber)
        {
            _subscribers.Add(subscriber);
        }

        public void Unsubscribe(Func<SyncEvent, Task> subscriber)
        {
            _subscribers.Remove(subscriber);
        }

        public List<SyncEvent> GetSyncEvents(int tenantId, DateTime lastSyncDate)
        {
            return SyncEvents.Where(item => (item.TenantId == tenantId || item.TenantId == -1) && item.ModifiedOn >= lastSyncDate).ToList();
        }

        public async Task AddSyncEventAsync(Alias alias, string entityName, int entityId, string action)
        {
            await AddSyncEventAsync(alias.TenantId, alias.SiteId, entityName, entityId, action);
        }

        public async Task AddSyncEventAsync(int tenantId, int siteId, string entityName, int entityId, string action)
        {
            var syncevent = new SyncEvent { TenantId = tenantId, SiteId = siteId, EntityName = entityName, EntityId = entityId, Action = action, ModifiedOn = DateTime.UtcNow };

            // client actions for PageState management
            if (action == SyncEventActions.Refresh || action == SyncEventActions.Reload)
            {
                // trim sync events
                SyncEvents.RemoveAll(item => item.ModifiedOn < DateTime.UtcNow.AddHours(-1));

                // add sync event
                SyncEvents.Add(syncevent);
            }

            // raise event - awaited in order, so a subscriber's work is done before the caller continues.
            // The snapshot preserves the semantics of the multicast delegate this replaced: an invocation list
            // is immutable, so subscribing or unsubscribing during a raise never disturbed the raise in flight.
            foreach (var subscriber in _subscribers.ToArray())
            {
                await subscriber(syncevent);
            }
        }


        // deprecated
        public async Task AddSyncEventAsync(int tenantId, string entityName, int entityId, string action)
        {
            await AddSyncEventAsync(tenantId, -1, entityName, entityId, action);
        }
    }
}
