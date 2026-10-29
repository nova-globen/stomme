using System;
using Microsoft.AspNetCore.Http;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// Once per browser per framework release, a page response tells the browser to drop its HTTP cache.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A release changes files whose URLs do not change - a package's <c>/_content/</c> assets, the Blazor JS initializers -
    /// and Stomme served them with no <c>Cache-Control</c>, so a browser kept them by heuristic freshness: a fraction of the
    /// age the file's <c>Last-Modified</c> gives it, and a package file carries the package's own date. Measured on the move to
    /// FluentUI 5 (2026-10-02): the host answered 457,618 bytes of 5.0.0's module, the browser kept running its 395,429 bytes of
    /// 4.14.3's, even through a hard reload (Blazor imports the module itself), and no FluentUI 5 control started.
    /// <see cref="FrameworkAssetCaching"/> now has those files revalidated, but that reaches only what a browser asks for again;
    /// a copy it already holds as fresh it does not ask about. This clears that copy, once.
    /// </para>
    /// <para>
    /// The cookie only remembers which release this browser has seen; it holds nothing about the visitor and is set whatever
    /// the cookie consent says, as the culture cookie is. A first visit sends the header too, with nothing to clear.
    /// </para>
    /// </remarks>
    internal static class ReleaseAssetCache
    {
        public const string CookieName = "stomme-release";

        /// <summary>The <c>Clear-Site-Data</c> value: the HTTP cache only, never cookies or storage.</summary>
        public const string ClearCache = "\"cache\"";

        /// <summary>Returns true when this response asks the browser to clear its cache.</summary>
        public static bool Apply(HttpContext context, string release)
        {
            if (context is null || string.IsNullOrEmpty(release) || context.Response.HasStarted)
            {
                return false;
            }

            if (context.Request.Cookies.TryGetValue(CookieName, out var seen) && string.Equals(seen, release, StringComparison.Ordinal))
            {
                return false;
            }

            context.Response.Headers["Clear-Site-Data"] = ClearCache;
            context.Response.Cookies.Append(CookieName, release, new CookieOptions
            {
                Path = "/",
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps,
                HttpOnly = true,
                IsEssential = true
            });
            return true;
        }
    }
}
