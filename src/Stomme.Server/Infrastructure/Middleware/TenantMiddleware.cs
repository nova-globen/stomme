using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Infrastructure
{
    internal class TenantMiddleware
    {
        private readonly RequestDelegate _next;

        public TenantMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task Invoke(HttpContext context)
        {
            // check if framework is installed
            var config = context.RequestServices.GetService(typeof(IConfigManager)) as IConfigManager;
            string path = context.Request.Path.ToString();

            if (config.IsInstalled()) 
            {
                // get alias (note that this also sets SiteState.Alias)
                var tenantManager = context.RequestServices.GetService(typeof(ITenantManager)) as ITenantManager;
                var alias = await tenantManager.GetAliasAsync();

                // A Host that is no configured alias gets a plain 404 and nothing is written: no alias is
                // registered for it and no page, API or asset of any site is served under it.
                if (alias == null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                if (alias != null)
                {
                    // save alias in HttpContext
                    context.Items.Add(Constants.HttpContextAliasKey, alias);

                    // save site settings in HttpContext
                    var cache = context.RequestServices.GetService(typeof(ICacheManager)) as ICacheManager;
                    var sitesettings = await cache.GetCacheAsync(alias, Constants.HttpContextSiteSettingsKey, async ct =>
                    {
                        var settingRepository = context.RequestServices.GetService(typeof(ISettingRepository)) as ISettingRepository;
                        return (await settingRepository.GetSettingsAsync(EntityNames.Site, alias.SiteId, EntityNames.Host, -1))
                            .ToDictionary(setting => setting.SettingName, setting => setting.SettingValue);
                    });
                    context.Items.Add(Constants.HttpContextSiteSettingsKey, sitesettings);

                    // rewrite path by removing alias path prefix from reserved route (api,pages,files) requests for consistent routes
                    if (!string.IsNullOrEmpty(alias.Path))
                    {
                        // A folder site is its own authority at /<folder>/: its connect/* and .well-known/* requests keep the
                        // folder as their PathBase, from which OpenIddict derives the issuer (https://host/<folder>/).
                        if (IsAuthorityRequest(path, alias.Path, out var authorityPath))
                        {
                            context.Request.PathBase = context.Request.PathBase.Add("/" + alias.Path.Trim('/'));
                            context.Request.Path = authorityPath;
                        }
                        else if (path.StartsWith("/" + alias.Path) && (Constants.ReservedRoutes.Any(item => path.Contains("/" + item + "/"))))
                        {
                            context.Request.Path = path.Substring(alias.Path.Length + 1);

                            // An API call with an access token keeps the folder as its PathBase too: the issuer OpenIddict
                            // expects of the token is derived from it, before any endpoint runs, so only this site's token passes.
                            if (context.Request.Path.StartsWithSegments("/api", System.StringComparison.OrdinalIgnoreCase) &&
                                context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", System.StringComparison.OrdinalIgnoreCase))
                            {
                                context.Request.PathBase = context.Request.PathBase.Add("/" + alias.Path.Trim('/'));
                            }
                        }
                        // A folder site's interactive circuit connects under the folder (Stomme.Server.lib.module.js), so the
                        // circuit request resolves this site and carries this site's cookies; the hub itself is the host's.
                        else if (path.Equals("/" + alias.Path + "/_blazor", System.StringComparison.OrdinalIgnoreCase) ||
                            path.StartsWith("/" + alias.Path + "/_blazor/", System.StringComparison.OrdinalIgnoreCase))
                        {
                            context.Request.Path = path.Substring(alias.Path.Length + 1);
                        }
                    }

                    // handle sitemap.xml request
                    if (context.Request.Path.ToString().Contains("/sitemap.xml") && !context.Request.Path.ToString().Contains("/pages"))
                    {
                        // A folder site keeps its folder as the PathBase (BL-265): the sitemap page is output-cached by path, and
                        // without it /folder/sitemap.xml shared the root's cache entry and answered the root's pages.
                        if (!string.IsNullOrEmpty(alias.Path) && context.Request.Path.StartsWithSegments("/" + alias.Path.Trim('/'), System.StringComparison.OrdinalIgnoreCase))
                        {
                            context.Request.PathBase = context.Request.PathBase.Add("/" + alias.Path.Trim('/'));
                        }

                        context.Request.Path = "/pages/sitemap.xml";
                    }

                    // handle robots.txt root request (does not support subfolder aliases)
                    if (context.Request.Path.StartsWithSegments("/robots.txt") && string.IsNullOrEmpty(alias.Path))
                    {
                        var robots = RobotsText(sitesettings.ContainsKey("Robots") ? sitesettings["Robots"] : null, context.Request.Scheme, alias.Name);
                        context.Response.ContentType = "text/plain";
                        await context.Response.WriteAsync(robots);
                        return;
                    }
                }
            }

            // continue processing
            if (_next != null) await _next(context);
        }

        /// <summary>
        /// Whether <paramref name="path"/> is a request to the folder site's own authority (<c>/folder/connect/...</c> or
        /// <c>/folder/.well-known/...</c>), and the path that remains below the folder.
        /// </summary>
        /// <summary>
        /// The site's robots.txt: its <c>Robots</c> setting, or <c>User-agent: *</c>, and a <c>Sitemap:</c> line unless the
        /// text already has one (in any case - BL-265: the old check lower-cased the text and then looked for "Sitemap:", so it
        /// never matched and appended a second line). The scheme is the request's after forwarded headers, so behind a proxy it
        /// is <c>https</c> only when the proxy sends <c>X-Forwarded-Proto</c>; a site can name its sitemap itself in the setting.
        /// </summary>
        internal static string RobotsText(string configured, string scheme, string aliasName)
        {
            var robots = string.IsNullOrEmpty(configured) ? "User-agent: *" : configured;
            if (robots.IndexOf("sitemap:", System.StringComparison.OrdinalIgnoreCase) < 0)
            {
                robots += $"\n\nSitemap: {scheme}://{aliasName}/sitemap.xml";
            }

            return robots;
        }

        internal static bool IsAuthorityRequest(string path, string aliasPath, out string authorityPath)
        {
            authorityPath = null;
            var prefix = "/" + aliasPath.Trim('/');
            if (!path.StartsWith(prefix + "/", System.StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var rest = path.Substring(prefix.Length);
            if (rest.StartsWith("/connect/", System.StringComparison.OrdinalIgnoreCase) ||
                rest.StartsWith("/.well-known/", System.StringComparison.OrdinalIgnoreCase))
            {
                authorityPath = rest;
                return true;
            }

            return false;
        }
    }
}
