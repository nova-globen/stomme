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
                        context.Request.Path = "/pages/sitemap.xml";
                    }

                    // handle robots.txt root request (does not support subfolder aliases)
                    if (context.Request.Path.StartsWithSegments("/robots.txt") && string.IsNullOrEmpty(alias.Path))
                    {
                        string robots = "";
                        if (sitesettings.ContainsKey("Robots") && !string.IsNullOrEmpty(sitesettings["Robots"]))
                        {
                            robots = sitesettings["Robots"];
                        }
                        else
                        {
                            // allow all user agents by default
                            robots = $"User-agent: *";
                        }
                        if (!robots.ToLower().Contains("Sitemap:"))
                        {
                            // add sitemap if not specified
                            robots += $"\n\nSitemap: {context.Request.Scheme}://{alias.Name}/sitemap.xml";
                        }
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
