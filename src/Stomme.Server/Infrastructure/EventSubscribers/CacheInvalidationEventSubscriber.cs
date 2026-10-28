using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Shared;

namespace Stomme.Infrastructure.EventSubscribers
{
    public class CacheInvalidationEventSubscriber : IEventSubscriber
    {
        private readonly ICacheManager _cache;

        public CacheInvalidationEventSubscriber(ICacheManager cache)
        {
            _cache = cache;
        }

        public async Task EntityChangedAsync(SyncEvent syncEvent)
        {
            // when site entities change (ie. site, pages, modules, etc...) a site refresh event is raised and the site cache item needs to be refreshed
            if (syncEvent.EntityName == EntityNames.Site && (syncEvent.Action == SyncEventActions.Refresh || syncEvent.Action == SyncEventActions.Reload))
            {
                await _cache.RemoveCacheAsync($"SiteKey:{syncEvent.SiteKey}:Site");
                await _cache.RemoveCacheAsync($"SiteKey:{syncEvent.SiteKey}:Modules");
            }

            // when a site entity is updated, the hosting model may have changed so the client assemblies cache items need to be refreshed
            if (syncEvent.EntityName == EntityNames.Site && (syncEvent.Action == SyncEventActions.Update || syncEvent.Action == SyncEventActions.Delete))
            {
                await _cache.RemoveCacheAsync($"SiteKey:{syncEvent.SiteKey}:AssembliesList");
                await _cache.RemoveCacheAsync($"SiteKey:{syncEvent.SiteKey}:Assemblies");
            }

            // when a users settings are changed, the user cache item needs to be refreshed
            if (syncEvent.EntityName == EntityNames.User && syncEvent.Action == SyncEventActions.Update)
            {
                await _cache.RemoveCacheAsync($"SiteKey:{syncEvent.SiteKey}:User:{syncEvent.EntityId}");
            }
        }
    }
}
