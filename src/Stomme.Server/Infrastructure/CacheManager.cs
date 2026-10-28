using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models;
using ZiggyCreatures.Caching.Fusion;

namespace Stomme.Infrastructure
{
    public class CacheManager : ICacheManager
    {
        private readonly IFusionCache _cache;

        public CacheManager(IFusionCache cache)
        {
            _cache = cache;
        }

        // asynchronous / global methods
        public async Task<T> GetCacheAsync<T>(string key, Func<CancellationToken, Task<T>> factory)
        {
            return await GetCacheAsync(null, key, factory);
        }

        public async Task<T> GetCacheAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan? memoryCacheDuration, TimeSpan? distributedCacheDuration)
        {
            return await GetCacheAsync(null, key, factory, memoryCacheDuration, distributedCacheDuration);
        }

        public async Task RemoveCacheAsync(string key)
        {
            await RemoveCacheAsync(null, key);
        }

        // asynchronous / multi-tenant methods
        public async Task<T> GetCacheAsync<T>(Alias alias, string key, Func<CancellationToken, Task<T>> factory)
        {
            return await _cache.GetOrSetAsync(FormatKey(alias, key),
                async ct => await factory(ct));
        }

        public async Task<T> GetCacheAsync<T>(Alias alias, string key, Func<CancellationToken, Task<T>> factory, TimeSpan? memoryCacheDuration, TimeSpan? distributedCacheDuration)
        {
            return await _cache.GetOrSetAsync(FormatKey(alias, key),
                async ct => await factory(ct),
                SetOptions(memoryCacheDuration, distributedCacheDuration));
        }

        public async Task RemoveCacheAsync(Alias alias, string key)
        {
            await _cache.RemoveAsync(FormatKey(alias, key));
        }

        public async Task ClearAllAsync()
        {
            // allowFailSafe: false - a REMOVAL. The default overload only expires entries logically, and fail-safe
            // (on in a stock install) then serves the cleared value to a failing factory (CacheClearAllTests).
            await _cache.ClearAsync(allowFailSafe: false);
        }

        private string FormatKey(Alias alias, string key)
        {
            if (alias != null)
            {
                key = $"SiteKey:{alias.SiteKey}:{key}";
            }
            return key;
        }

        private FusionCacheEntryOptions SetOptions(TimeSpan? memoryCacheDuration, TimeSpan? distributedCacheDuration)
        {
            var options = new FusionCacheEntryOptions();
            options.MemoryCacheDuration = memoryCacheDuration; // infinite = TimeSpan.MaxValue
            if (distributedCacheDuration == TimeSpan.MinValue)
            {
                options.SetSkipDistributedCache(true, true);
            }
            else
            {
                options.DistributedCacheDuration = distributedCacheDuration;
            }
            return options;
        }
    }
}
