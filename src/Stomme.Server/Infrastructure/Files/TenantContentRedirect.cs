using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Stomme.Extensions;
using Stomme.Shared;

namespace Stomme.Infrastructure.Files
{
    /// <summary>
    /// BL-246 - a Public-type folder's bytes are never served past their folder's permissions. A Public folder lives under
    /// the web root (<c>wwwroot/Content/Tenants/{tenant}/Sites/{site}/...</c>), and the static-file middleware served it
    /// to anyone: driven, a file in a Public folder whose View was Administrators only answered 403 at <c>/files/...</c>
    /// and 200 with its bytes at its static path, the ids being small integers. The static-file middleware now never
    /// serves that tree, and a request for it is sent to the same file on the checked <c>/files/</c> route of the site it
    /// belongs to - so a link to a truly public file still works, and every other answers as its folder says. A path of
    /// another tenant or site than the one the request resolved to is not found.
    /// </summary>
    public static class TenantContentRedirect
    {
        private static readonly PathString TenantContent = new("/Content/Tenants");

        public static bool IsTenantContent(PathString path) => path.StartsWithSegments(TenantContent, StringComparison.OrdinalIgnoreCase);

        /// <summary>Placed after tenant resolution: the alias decides which site's file route the request goes to.</summary>
        public static IApplicationBuilder UseTenantContentRedirect(this IApplicationBuilder app) => app.Use(async (context, next) =>
        {
            if (!IsTenantContent(context.Request.Path))
            {
                await next();
                return;
            }

            // Content/Tenants/{tenant}/Sites/{site}/{folder path}/{file name}
            var segments = context.Request.Path.Value!.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var alias = context.GetAlias();
            if (alias == null || segments.Length < 7 || !string.Equals(segments[3], "Sites", StringComparison.OrdinalIgnoreCase)
                || segments[2] != alias.TenantId.ToString() || segments[4] != alias.SiteId.ToString())
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var folderPath = string.Join('/', segments, 5, segments.Length - 6) + "/";
            context.Response.Redirect(Utilities.FileUrl(alias, folderPath, segments[^1]), permanent: false);
        });
    }
}
