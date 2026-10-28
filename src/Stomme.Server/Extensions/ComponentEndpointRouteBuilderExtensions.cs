#nullable enable
using Stomme.Components;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.AspNetCore.Routing;
using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Net.Http.Headers;

namespace StommeSSR.Extensions
{
    public static class ComponentEndpointRouteBuilderExtensions
    {
        public static void MapFallback(this IEndpointRouteBuilder endpoints)
        {
            ArgumentNullException.ThrowIfNull(endpoints);

            endpoints.MapFallback((ctx) =>
            {
                // BL-223: a page is a GET, a HEAD or a form's POST. Anything else that reaches the fallback - a hub's negotiate
                // with the transport off, a JSON or body-less POST to a path nothing serves - is a 404, never the page renderer,
                // which answered such a request 500 (measured on UAT and in SwitchedOffApiNotFoundTests; a form POST it refuses
                // itself, 400).
                if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && !IsFormPost(ctx.Request.ContentType))
                {
                    // BL-227: a bare 404, NOT re-executed as the site's 404 page. Status-code pages re-run the pipeline for
                    // /404 with the same method and the same antiforgery state, and that page endpoint failed on a token-less
                    // POST exactly as this one did - 500 again (measured: the header check alone still answered 500).
                    var statusCodePages = ctx.Features.Get<IStatusCodePagesFeature>();
                    if (statusCodePages is not null)
                    {
                        statusCodePages.Enabled = false;
                    }

                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return Task.CompletedTask;
                }

                var invoker = ctx.RequestServices.GetRequiredService<IRazorComponentEndpointInvoker>();
                return invoker.Render(ctx);
            })
            .Add(routeEndpointBuilder =>
            {
                routeEndpointBuilder.Metadata.Add(new RootComponentMetadata(typeof(App)));
                routeEndpointBuilder.Metadata.Add(new ComponentTypeMetadata(typeof(App)));
                routeEndpointBuilder.Metadata.Add(new RequireAntiforgeryTokenAttribute());
            });
        }

        // BL-227: the HEADER, never Request.HasFormContentType. This endpoint requires an antiforgery token, so for a POST
        // without one the antiforgery middleware has already set an invalid IAntiforgeryValidationFeature, and
        // HasFormContentType THROWS on it - every token-less POST answered 500 and logged an error (measured on UAT on 10.7.0).
        internal static bool IsFormPost(string? contentType) =>
            MediaTypeHeaderValue.TryParse(contentType, out var type) &&
            (type.MediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) ||
             type.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase));
    }
}
