#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.CostOptimization;
using Stomme.Infrastructure;
using Stomme.Models;
using Stomme.Modules.CostOptimization.Application;
using Stomme.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Tests.CostOptimization
{
    /// <summary>
    /// The <c>ModelResponseCachingTests</c> cases, ported by name (9 cases): the key cases unchanged; the cache-aside
    /// cases over <see cref="ICacheManager"/> - CHANGED: an entry is the site's (stored under its alias) and its TTL is the
    /// memory and distributed duration; the <c>AiResponse</c> tag has no equivalent. The port's own: two sites never
    /// share an answer, and with no site the cache is bypassed rather than shared.
    /// </summary>
    public sealed class ModelResponseCachingTests
    {
        private static readonly ModelPromptMessage[] Messages =
        {
            new("system", "You are the assistant."),
            new("user", "list my datasets"),
        };

        // --- Key determinism and sensitivity ---

        [Fact]
        public void Key_is_deterministic_for_identical_inputs()
        {
            var first = ModelResponseCacheKey.Create("anthropic", "model-a", Messages, 0f);
            var second = ModelResponseCacheKey.Create("anthropic", "model-a", Messages, 0f);

            Assert.Equal(first.Value, second.Value);
            // 64-char lowercase hex - a SHA-256 digest.
            Assert.Equal(64, first.Value.Length);
            Assert.Matches("^[0-9a-f]{64}$", first.Value);
        }

        [Fact]
        public void Key_changes_when_any_tuple_component_changes()
        {
            var baseline = ModelResponseCacheKey.Create("anthropic", "model-a", Messages, 0f).Value;

            // Provider, model, temperature.
            Assert.NotEqual(baseline, ModelResponseCacheKey.Create("openai", "model-a", Messages, 0f).Value);
            Assert.NotEqual(baseline, ModelResponseCacheKey.Create("anthropic", "model-b", Messages, 0f).Value);
            Assert.NotEqual(baseline, ModelResponseCacheKey.Create("anthropic", "model-a", Messages, -1f).Value);

            // Message content, role, and order are all significant.
            ModelPromptMessage[] changedContent = { Messages[0], new("user", "list my dashboards") };
            ModelPromptMessage[] changedRole = { Messages[0], new("assistant", "list my datasets") };
            ModelPromptMessage[] reordered = { Messages[1], Messages[0] };
            Assert.NotEqual(baseline, ModelResponseCacheKey.Create("anthropic", "model-a", changedContent, 0f).Value);
            Assert.NotEqual(baseline, ModelResponseCacheKey.Create("anthropic", "model-a", changedRole, 0f).Value);
            Assert.NotEqual(baseline, ModelResponseCacheKey.Create("anthropic", "model-a", reordered, 0f).Value);
        }

        [Fact]
        public void Key_has_no_field_boundary_collisions()
        {
            // One message "ab" vs two messages "a" + "b": naive concatenation would collide; length-prefixing must not.
            var joined = ModelResponseCacheKey.Create("p", "m", new[] { new ModelPromptMessage("user", "ab") }, 0f);
            var split = ModelResponseCacheKey.Create("p", "m", new[] { new ModelPromptMessage("user", "a"), new ModelPromptMessage("user", "b") }, 0f);

            Assert.NotEqual(joined.Value, split.Value);
        }

        // --- Cache-aside round trip over the cache manager ---

        [Fact]
        public async Task Second_call_for_the_same_key_is_served_from_the_cache_without_running_the_factory()
        {
            var cache = new RecordingCacheManager();
            var responseCache = Build(cache, Site(1));
            var key = ModelResponseCacheKey.Create("anthropic", "model-a", Messages, 0f);
            var factoryRuns = 0;
            Task<CachedModelResponse> Factory(CancellationToken _)
            {
                factoryRuns++;
                return Task.FromResult(new CachedModelResponse("cached answer", "model-a", 10, 5));
            }

            var miss = await responseCache.GetOrCreateAsync(key, Factory);
            var hit = await responseCache.GetOrCreateAsync(key, Factory);

            Assert.False(miss.FromCache);
            Assert.True(hit.FromCache);
            Assert.Equal(1, factoryRuns);
            Assert.Equal(miss.Response, hit.Response);
            Assert.Equal("cached answer", hit.Response.Text);
        }

        // CHANGED: the Entries_carry_the_configured_ttl_and_the_AiResponse_tag case - the cache manager carries no tag, so the
        // entry carries the TTL as both durations and the SITE's alias instead.
        [Fact]
        public async Task Entries_carry_the_configured_ttl_and_the_sites_alias()
        {
            var cache = new RecordingCacheManager();
            var responseCache = Build(cache, Site(3), new ResponseCacheOptions { TtlHours = 6 });

            await responseCache.GetOrCreateAsync(ModelResponseCacheKey.Create("anthropic", "model-a", Messages, 0f),
                _ => Task.FromResult(new CachedModelResponse("x", "model-a", 1, 1)));

            var call = Assert.Single(cache.Calls);
            Assert.Equal((TimeSpan.FromHours(6), TimeSpan.FromHours(6)), (call.Memory, call.Distributed));
            Assert.Equal(3, call.SiteId);
            Assert.StartsWith(CacheManagerModelResponseCache.KeyPrefix, call.Key, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(false, 24)] // disabled by the switch
        [InlineData(true, 0)] // disabled by a non-positive TTL
        public async Task Disabled_cache_bypasses_the_store_and_always_runs_the_factory(bool enabled, int ttlHours)
        {
            var cache = new RecordingCacheManager();
            var responseCache = Build(cache, Site(1), new ResponseCacheOptions { Enabled = enabled, TtlHours = ttlHours });
            var key = ModelResponseCacheKey.Create("anthropic", "model-a", Messages, 0f);
            var factoryRuns = 0;
            Task<CachedModelResponse> Factory(CancellationToken _)
            {
                factoryRuns++;
                return Task.FromResult(new CachedModelResponse("x", "model-a", 1, 1));
            }

            var first = await responseCache.GetOrCreateAsync(key, Factory);
            var second = await responseCache.GetOrCreateAsync(key, Factory);

            Assert.False(first.FromCache);
            Assert.False(second.FromCache);
            Assert.Equal(2, factoryRuns);
            Assert.Empty(cache.Entries);
        }

        // --- Options binding + registration ---

        // CHANGED: the section is CostOptimization:ResponseCache in the HOST's configuration, composed through the module's startup.
        [Fact]
        public async Task AddCostOptimization_binds_the_response_cache_options_and_resolves_the_cache()
        {
            await using var harness = await CostOptimizationApplicationHarness.StartAsync(new Dictionary<string, string?>
            {
                ["CostOptimization:ResponseCache:Enabled"] = "false",
                ["CostOptimization:ResponseCache:TtlHours"] = "6",
            }, services => services.AddSingleton<ICacheManager>(new RecordingCacheManager())); // the host's cache manager

            var (options, cache) = await harness.InScopeAsync(sp => Task.FromResult((sp.GetRequiredService<ResponseCacheOptions>(), sp.GetRequiredService<IModelResponseCache>())));

            Assert.False(options.Enabled);
            Assert.Equal(6, options.TtlHours);
            Assert.IsType<CacheManagerModelResponseCache>(cache);
        }

        [Fact]
        public void Response_cache_options_default_to_enabled_with_a_24_hour_ttl()
        {
            var options = new ResponseCacheOptions();

            Assert.True(options.Enabled);
            Assert.Equal(24, options.TtlHours);
        }

        // --- The port's own ---

        [Fact]
        public async Task Two_sites_sending_the_same_prompt_never_share_an_answer()
        {
            var cache = new RecordingCacheManager();
            var key = ModelResponseCacheKey.Create("anthropic", "model-a", Messages, 0f);

            var first = await Build(cache, Site(1)).GetOrCreateAsync(key, _ => Task.FromResult(new CachedModelResponse("site one's answer", "model-a", 1, 1)));
            var second = await Build(cache, Site(2)).GetOrCreateAsync(key, _ => Task.FromResult(new CachedModelResponse("site two's answer", "model-a", 1, 1)));

            Assert.Equal((false, "site two's answer"), (second.FromCache, second.Response.Text));
            Assert.Equal(2, cache.Entries.Count);
            Assert.False(first.FromCache);
        }

        [Fact]
        public async Task With_no_site_the_cache_is_bypassed_rather_than_shared()
        {
            var cache = new RecordingCacheManager();
            var responseCache = Build(cache, new SiteState());

            var result = await responseCache.GetOrCreateAsync(ModelResponseCacheKey.Create("anthropic", "model-a", Messages, 0f),
                _ => Task.FromResult(new CachedModelResponse("x", "model-a", 1, 1)));

            Assert.False(result.FromCache);
            Assert.Empty(cache.Calls);
        }

        private static SiteState Site(int siteId) => new() { Alias = new Alias { AliasId = siteId, TenantId = 1, SiteId = siteId, Name = "site" + siteId } };

        private static CacheManagerModelResponseCache Build(ICacheManager cache, SiteState site, ResponseCacheOptions? options = null) =>
            new(cache, options ?? new ResponseCacheOptions(), site);

        /// <summary>An in-memory <see cref="ICacheManager"/> keyed as the real one keys an alias's entry, recording each call.</summary>
        private sealed class RecordingCacheManager : ICacheManager
        {
            public Dictionary<string, object?> Entries { get; } = new();

            public List<(string Key, int SiteId, TimeSpan? Memory, TimeSpan? Distributed)> Calls { get; } = new();

            public Task<T> GetCacheAsync<T>(string key, Func<CancellationToken, Task<T>> factory) => throw new NotSupportedException("the response cache is always a site's");

            public Task<T> GetCacheAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan? memoryCacheDuration, TimeSpan? distributedCacheDuration) =>
                throw new NotSupportedException("the response cache is always a site's");

            public Task RemoveCacheAsync(string key) => Task.CompletedTask;

            public Task<T> GetCacheAsync<T>(Alias alias, string key, Func<CancellationToken, Task<T>> factory) => GetCacheAsync(alias, key, factory, null, null);

            public async Task<T> GetCacheAsync<T>(Alias alias, string key, Func<CancellationToken, Task<T>> factory, TimeSpan? memoryCacheDuration, TimeSpan? distributedCacheDuration)
            {
                Calls.Add((key, alias.SiteId, memoryCacheDuration, distributedCacheDuration));
                var stored = $"SiteKey:{alias.SiteKey}:{key}";
                if (Entries.TryGetValue(stored, out var existing))
                {
                    return (T)existing!;
                }

                var created = await factory(CancellationToken.None);
                Entries[stored] = created;
                return created;
            }

            public Task RemoveCacheAsync(Alias alias, string key) => Task.CompletedTask;

            public Task ClearAllAsync()
            {
                Entries.Clear();
                return Task.CompletedTask;
            }
        }
    }
}
