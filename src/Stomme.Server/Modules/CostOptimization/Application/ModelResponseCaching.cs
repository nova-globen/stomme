#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Stomme.Infrastructure;
using Stomme.Shared;
using Microsoft.Extensions.Configuration;

namespace Stomme.Modules.CostOptimization.Application
{
    /// <summary>
    /// The response cache's settings (the predecessor platform's <c>ResponseCacheOptions</c>), read from the HOST's configuration section
    /// <c>CostOptimization:ResponseCache</c> (BL-162): on by default with a 24-hour TTL, a non-positive TTL disabling it.
    /// Nothing calls the cache until the AI assistant exists (BL-97 increments 9-10), so the default changes nothing today.
    /// </summary>
    public sealed class ResponseCacheOptions
    {
        public const string SectionName = "CostOptimization:ResponseCache";

        public bool Enabled { get; set; } = true;

        public int TtlHours { get; set; } = 24;

        public static ResponseCacheOptions From(IConfiguration? configuration)
        {
            var options = new ResponseCacheOptions();
            configuration?.GetSection(SectionName).Bind(options);
            return options;
        }
    }

    /// <summary>
    /// The deterministic LLM response cache (BL-97 increment 8, the predecessor platform's <c>CachedModelResponseCache</c>) over the
    /// framework's <see cref="ICacheManager"/>: a hit returns the stored response without running the factory; a miss runs
    /// it once and stores the answer for the TTL.
    /// </summary>
    /// <remarks>
    /// <b>CHANGED from the predecessor platform:</b> an entry belongs to the REQUEST's site - it is stored under the site's alias, so two
    /// sites (or tenants) sending the same prompt never share an answer; the predecessor platform's key was installation-wide. With no site
    /// resolved the cache is bypassed rather than shared. the predecessor platform's <c>AiResponse</c> tag (its bulk-eviction handle) has no
    /// equivalent - <see cref="ICacheManager"/> carries no tags - so the TTL is what retires an entry, and the predecessor platform's
    /// stale-cache eviction job is not ported.
    /// </remarks>
    public sealed class CacheManagerModelResponseCache(ICacheManager cache, ResponseCacheOptions options, SiteState siteState) : IModelResponseCache
    {
        public const string KeyPrefix = "ai:response:";

        public async Task<ModelResponseCacheResult> GetOrCreateAsync(ModelResponseCacheKey key, Func<CancellationToken, Task<CachedModelResponse>> factory, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(factory);

            var alias = siteState.Alias;
            if (!options.Enabled || options.TtlHours <= 0 || alias is null || alias.SiteId <= 0)
            {
                return new ModelResponseCacheResult(await factory(ct), FromCache: false);
            }

            var fromCache = true;
            var ttl = TimeSpan.FromHours(options.TtlHours);
            var response = await cache.GetCacheAsync(alias, KeyPrefix + key.Value, async token =>
            {
                fromCache = false;
                return await factory(token);
            }, ttl, ttl);
            return new ModelResponseCacheResult(response, fromCache);
        }
    }
}
