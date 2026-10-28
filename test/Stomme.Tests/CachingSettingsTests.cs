using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-251 - a partial <c>Caching</c> section still caches. With any key under <c>Caching</c> the host read every value as
    /// given, so <c>{"Caching": {"Distributed": true}}</c> meant a memory duration of 0 and fail-safe off: nothing cached.
    /// Each value now defaults on its own, to what an absent section gives.
    /// </summary>
    public sealed class CachingSettingsTests
    {
        // The class name exists in Stomme.Client too, so the server's copy is reached through its assembly.
        private static ZiggyCreatures.Caching.Fusion.FusionCacheEntryOptions Defaults(IConfigurationSection section) =>
            (ZiggyCreatures.Caching.Fusion.FusionCacheEntryOptions)typeof(Stomme.Infrastructure.HostConfiguration).Assembly
                .GetType("Microsoft.Extensions.DependencyInjection.StommeServiceCollectionExtensions", throwOnError: true)!
                .GetMethod("CacheEntryDefaults", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(null, new object[] { section })!;

        private static IConfigurationSection Section(Dictionary<string, string> values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection("Caching");

        [Fact]
        public void A_partial_section_keeps_the_defaults_it_does_not_name()
        {
            var absent = Defaults(Section(new()));
            var partial = Defaults(Section(new() { ["Caching:Distributed"] = "true" }));

            foreach (var options in new[] { absent, partial })
            {
                Assert.Equal(TimeSpan.FromMinutes(10), options.MemoryCacheDuration);
                Assert.True(options.IsFailSafeEnabled);
                Assert.Equal(TimeSpan.FromMinutes(30), options.FailSafeMaxDuration);
                Assert.Equal(TimeSpan.FromMinutes(2), options.FailSafeThrottleDuration);
                Assert.Equal(TimeSpan.FromMinutes(30), options.DistributedCacheDuration);
            }
        }

        [Fact]
        public void Values_the_section_names_are_used()
        {
            var options = Defaults(Section(new() { ["Caching:Duration"] = "20", ["Caching:FailSafe"] = "false" }));

            Assert.Equal(TimeSpan.FromMinutes(20), options.MemoryCacheDuration);
            Assert.False(options.IsFailSafeEnabled);
            Assert.Equal(TimeSpan.FromMinutes(60), options.DistributedCacheDuration);
        }
    }
}
