using System.Linq;
using Stomme.Infrastructure;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// Once per browser per release, a page response clears the browser's HTTP cache, so an asset whose URL a release
    /// does not change (measured: FluentUI's module, 4.14.3 kept after the move to 5.0.0) is fetched again.
    /// </summary>
    public sealed class ReleaseAssetCacheTests
    {
        [Fact]
        public void A_browser_that_has_not_seen_this_release_is_told_to_clear_its_cache_and_remembers_the_release()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers.Cookie = ReleaseAssetCache.CookieName + "=10.4.1";

            Assert.True(ReleaseAssetCache.Apply(context, "10.5.0"));
            Assert.Equal(ReleaseAssetCache.ClearCache, context.Response.Headers["Clear-Site-Data"].ToString());
            var cookie = context.Response.Headers.SetCookie.ToString();
            Assert.Contains(ReleaseAssetCache.CookieName + "=10.5.0", cookie);
            Assert.Contains("httponly", cookie.ToLowerInvariant());
        }

        [Fact]
        public void A_first_visit_is_told_too()
        {
            var context = new DefaultHttpContext();
            Assert.True(ReleaseAssetCache.Apply(context, "10.5.0"));
            Assert.Equal(ReleaseAssetCache.ClearCache, context.Response.Headers["Clear-Site-Data"].ToString());
        }

        [Fact]
        public void A_browser_that_has_seen_this_release_keeps_its_cache()
        {
            var context = new DefaultHttpContext();
            context.Request.Headers.Cookie = ReleaseAssetCache.CookieName + "=10.5.0";

            Assert.False(ReleaseAssetCache.Apply(context, "10.5.0"));
            Assert.False(context.Response.Headers.ContainsKey("Clear-Site-Data"));
            Assert.Empty(context.Response.Headers.SetCookie.Where(value => value.Contains(ReleaseAssetCache.CookieName)));
        }

        [Fact]
        public void Only_the_cache_is_cleared_never_cookies_or_storage()
        {
            // "cookies" would sign every visitor out on every release, "storage" would drop their colour mode.
            Assert.Equal("\"cache\"", ReleaseAssetCache.ClearCache);
        }
    }
}
