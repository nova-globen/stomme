using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Stomme.Models;

namespace Stomme.Extensions
{
    /// <summary>
    /// A module's middleware or filter for ITS site only (M2 row 10, ruling C33). An <c>IServerPipelineStartup</c> hook and
    /// a global MVC filter see every request on every site of the host; one image serves several sites, so a module that
    /// rewrites, authenticates or limits must scope itself. These read the site the request was resolved to, so they work
    /// at the stages AFTER tenant resolution (<c>BeforeJwt</c>, <c>BeforeRouting</c>) and in filters - never at
    /// <c>BeforeStaticFiles</c> or <c>BeforeTenantResolution</c>, where no site is known yet (they then match nothing).
    /// </summary>
    public static class SiteScopedPipelineExtensions
    {
        /// <summary>Runs <paramref name="configuration"/>'s middleware only for requests resolved to a site <paramref name="predicate"/> accepts.</summary>
        public static IApplicationBuilder UseWhenSite(this IApplicationBuilder app, Func<Alias, bool> predicate, Action<IApplicationBuilder> configuration)
        {
            ArgumentNullException.ThrowIfNull(predicate);
            return app.UseWhen(context => context.GetAlias() is { } alias && predicate(alias), configuration);
        }

        /// <summary>
        /// Runs <paramref name="configuration"/>'s middleware only for requests to the site in the folder
        /// <paramref name="sitePath"/> (<c>"didin"</c> for <c>host/didin</c>, on every host name; <c>""</c> for a root site).
        /// </summary>
        public static IApplicationBuilder UseWhenSitePath(this IApplicationBuilder app, string sitePath, Action<IApplicationBuilder> configuration) =>
            app.UseWhenSite(alias => IsPath(alias, sitePath), configuration);

        /// <summary>True when the request was resolved to the site in the folder <paramref name="sitePath"/> - for a filter or an endpoint.</summary>
        public static bool IsSitePath(this HttpContext context, string sitePath) =>
            context.GetAlias() is { } alias && IsPath(alias, sitePath);

        private static bool IsPath(Alias alias, string sitePath) =>
            string.Equals((alias.Path ?? string.Empty).Trim('/'), (sitePath ?? string.Empty).Trim('/'), StringComparison.OrdinalIgnoreCase);
    }
}
