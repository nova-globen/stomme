using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// The framework's cache surface over FusionCache.
    /// </summary>
    /// <remarks>
    /// ASYNC ONLY. The synchronous <c>GetCache</c>/<c>RemoveCache</c> overloads were removed: their
    /// factory was a SYNC <c>Func&lt;CancellationToken, T&gt;</c>, so a cache miss whose factory had to read
    /// a store — which is every factory the framework passes — could only satisfy it by blocking. That is
    /// the exact shape D29 describes: <c>UserManager.GetUser</c> reached the store through a sync cache
    /// callback, and on the renderer's pump the wait never returned. Removing the sync surface removes the
    /// only way to reintroduce it.
    /// </remarks>
    public interface ICacheManager
    {
        // asynchronous / global methods
        Task<T> GetCacheAsync<T>(string key, Func<CancellationToken, Task<T>> factory);
        Task<T> GetCacheAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan? memoryCacheDuration, TimeSpan? distributedCacheDuration);
        Task RemoveCacheAsync(string key);

        // asynchronous / multi-tenant methods
        Task<T> GetCacheAsync<T>(Alias alias, string key, Func<CancellationToken, Task<T>> factory);
        Task<T> GetCacheAsync<T>(Alias alias, string key, Func<CancellationToken, Task<T>> factory, TimeSpan? memoryCacheDuration, TimeSpan? distributedCacheDuration);
        Task RemoveCacheAsync(Alias alias, string key);

        /// <summary>
        /// Removes EVERY entry, for every tenant and site - the SystemInfo Caching section's "Clear All" (BL-100 O1).
        /// </summary>
        /// <remarks>
        /// A real removal, not a logical expiry: with fail-safe on (the stock default) an expired entry is served back
        /// to a failing factory, so a "clear" that only expired entries would keep serving what it cleared exactly
        /// when the store behind it is broken (pinned by CacheClearAllTests).
        /// </remarks>
        Task ClearAllAsync();
    }
}
