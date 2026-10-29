#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// Keeps a FOLDER site's framework cookies to itself: a site at <c>host/shop</c> shares the host with the root site and
    /// every other folder site, and a cookie the root sets with <c>Path=/</c> reaches <c>/shop</c> too, under the same name - so without this one sign-in, one antiforgery token and one release marker
    /// served every site on the host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>On the way out</b> every framework cookie a folder site's response sets - the sign-in cookies
    /// (<c>.AspNetCore.Identity.*</c>, chunks included, and the site's own <c>LoginOptions:CookieName</c>), the antiforgery
    /// cookie and the release marker - is renamed <c>&lt;name&gt;-&lt;site key&gt;</c> (the alias path, <c>/</c> as
    /// <c>-</c>) and scoped to <c>Path=/&lt;alias path&gt;</c>. <b>On the way in</b> the site's own renamed cookies are
    /// handed to the framework under their usual names, and any same-named cookie the root site (or another site) left
    /// at a wider path is dropped, so a folder site never reads another site's sign-in. The framework code that sets and
    /// reads these cookies is unchanged; a root site is untouched.
    /// </para>
    /// <para>
    /// <b>Not here: the culture cookie.</b> It is set and read in the browser (<c>Interop.SetCookie</c>) and read before
    /// tenant resolution, so it keeps its name and takes the site's path instead; the browser sends the longer path first,
    /// and the first value wins.
    /// </para>
    /// </remarks>
    public sealed class FolderSiteCookieMiddleware
    {
        private static readonly Regex PathAttribute = new(@";\s*path=[^;]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly RequestDelegate _next;

        public FolderSiteCookieMiddleware(RequestDelegate next) => _next = next;

        public Task InvokeAsync(HttpContext context)
        {
            if (context.Items[Constants.HttpContextAliasKey] is not Alias { Path: { Length: > 0 } aliasPath } ||
                string.IsNullOrWhiteSpace(aliasPath))
            {
                return _next(context); // the root site, or no site: nothing to keep apart
            }

            var key = SiteKey(aliasPath);
            var names = FrameworkCookieNames(context.Items[Constants.HttpContextSiteSettingsKey] as Dictionary<string, string>);
            Translate(context, key, names);

            var path = "/" + aliasPath.Trim('/');
            context.Response.OnStarting(() =>
            {
                var setCookies = context.Response.Headers.SetCookie;
                if (setCookies.Count > 0)
                {
                    context.Response.Headers.SetCookie = new StringValues(setCookies.Select(value => Scope(value!, key, path, names)).ToArray());
                }

                return Task.CompletedTask;
            });
            return _next(context);
        }

        /// <summary>The suffix a folder site's cookies carry: its alias path, <c>/</c> written as <c>-</c>.</summary>
        public static string SiteKey(string aliasPath) => aliasPath.Trim('/').Replace('/', '-').ToLowerInvariant();

        /// <summary>The name prefixes of the framework cookies a folder site keeps to itself.</summary>
        public static IReadOnlyList<string> FrameworkCookieNames(IReadOnlyDictionary<string, string>? siteSettings)
        {
            var names = new List<string> { ".AspNetCore.Identity.", Constants.AntiForgeryTokenCookieName, ReleaseAssetCache.CookieName };
            if (siteSettings is not null && siteSettings.TryGetValue("LoginOptions:CookieName", out var own) && !string.IsNullOrWhiteSpace(own))
            {
                names.Add(own.Trim());
            }

            return names;
        }

        /// <summary>A <c>Set-Cookie</c> value for a folder site: a framework cookie renamed and scoped to the site's path.</summary>
        public static string Scope(string setCookie, string key, string path, IReadOnlyList<string> names)
        {
            var equals = setCookie.IndexOf('=');
            if (equals <= 0)
            {
                return setCookie;
            }

            var name = setCookie[..equals].Trim();
            if (!IsFramework(name, names) || name.EndsWith("-" + key, StringComparison.Ordinal))
            {
                return setCookie; // a module's own cookie, or one already scoped
            }

            var rest = PathAttribute.Replace(setCookie[equals..], string.Empty);
            return name + "-" + key + rest + "; path=" + path;
        }

        private static void Translate(HttpContext context, string key, IReadOnlyList<string> names)
        {
            var header = context.Request.Headers.Cookie;
            if (header.Count == 0)
            {
                return;
            }

            var translated = TranslateHeader(string.Join("; ", header.ToArray()), key, names);
            if (translated is not null)
            {
                context.Request.Headers.Cookie = translated;
                context.Features.Set<IRequestCookiesFeature>(new RequestCookiesFeature(context.Features));
            }
        }

        /// <summary>
        /// A folder site's <c>Cookie</c> header as the framework should read it: the site's own renamed cookies under their
        /// framework names, another site's same-named ones dropped, everything else untouched. Values are kept exactly as
        /// sent. Null when nothing changes.
        /// </summary>
        public static string? TranslateHeader(string cookieHeader, string key, IReadOnlyList<string> names)
        {
            var suffix = "-" + key;
            var pairs = cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(pair => (name: pair.Split('=', 2)[0].Trim(), raw: pair)).ToList();
            if (!pairs.Any(pair => IsFramework(pair.name, names)))
            {
                return null;
            }

            // The site's own come back under their framework names and win over a wider same-named one, whatever the order
            // the browser sent them in; another site's framework cookies are dropped.
            var own = pairs.Where(pair => pair.name.EndsWith(suffix, StringComparison.Ordinal) && IsFramework(pair.name[..^suffix.Length], names))
                .Select(pair => pair.name[..^suffix.Length] + pair.raw[pair.name.Length..].TrimStart());
            var others = pairs.Where(pair => !IsFramework(pair.name, names)).Select(pair => pair.raw);
            return string.Join("; ", own.Concat(others));
        }

        /// <summary>
        /// The cookies a call the host makes to its own API on behalf of <paramref name="context"/> carries, as
        /// <c>name=value</c> pairs: the request's cookies as the browser sent them. On a folder site the framework's cookies
        /// reached the framework under their usual names (<see cref="TranslateHeader"/>); they go back under the site's names,
        /// or the self-call - translated again on its way in - would drop them as another site's and arrive with no sign-in
        /// and no antiforgery cookie.
        /// </summary>
        public static IEnumerable<string> SelfCallCookies(HttpContext context)
        {
            string? key = null;
            IReadOnlyList<string> names = Array.Empty<string>();
            if (context.Items[Constants.HttpContextAliasKey] is Alias { Path: { Length: > 0 } aliasPath } && !string.IsNullOrWhiteSpace(aliasPath))
            {
                key = SiteKey(aliasPath);
                names = FrameworkCookieNames(context.Items[Constants.HttpContextSiteSettingsKey] as Dictionary<string, string>);
            }

            foreach (var cookie in context.Request.Cookies)
            {
                var name = key is not null && IsFramework(cookie.Key, names) && !cookie.Key.EndsWith("-" + key, StringComparison.Ordinal)
                    ? cookie.Key + "-" + key
                    : cookie.Key;
                yield return name + "=" + System.Net.WebUtility.UrlEncode(cookie.Value);
            }
        }

        private static bool IsFramework(string name, IReadOnlyList<string> names) =>
            names.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));
    }
}
