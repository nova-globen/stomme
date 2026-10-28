using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using Stomme.Components;
using Stomme.Identity.OpenIddict;
using Stomme.Infrastructure;
using Stomme.Modules.Health.Infrastructure;
using Stomme.Infrastructure.Licensing;
using Stomme.Shared;
using Stomme.UI;
using StommeSSR.Extensions;

namespace Stomme.Extensions
{
    public static class ApplicationBuilderExtensions
    {
        /// <remarks>
        /// ASYNC because of its last statement: the startup sync event is raised through
        /// <see cref="ISyncManager.AddSyncEventAsync(int, int, string, int, string)"/>, whose subscribers are
        /// awaited. Raising it without awaiting would be the fire-and-forget the awaitable seam exists to
        /// remove. Nothing else in the pipeline build is async.
        /// </remarks>
        public static async Task<IApplicationBuilder> UseStommeAsync(this IApplicationBuilder app, IConfigurationRoot configuration, IWebHostEnvironment environment, ICorsService corsService, ICorsPolicyProvider corsPolicyProvider, ISyncManager sync)
        {
            // D105: validate the entitlement leg POST-BUILD and BEFORE anything starts — a stray or replaced
            // IEntitlementProvider, conflicting feature descriptors or a non-canonical RequiredEntitlement fail the boot
            // here, instead of inside FeatureSeeder.StartAsync's catch-all (which would log and leave evaluations to 500).
            EntitlementStartupValidation.Validate(app.ApplicationServices, configuration);

            ServiceActivator.Configure(app.ApplicationServices);

            if (environment.IsDevelopment())
            {
                app.UseWebAssemblyDebugging();
                app.UseForwardedHeaders();
            }
            else
            {
                app.UseForwardedHeaders();
                app.UseExceptionHandler("/Error", createScopeForErrors: true);
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            // allow Stomme localization middleware
            app.UseStommeLocalization();

            // The health endpoints answer for ANY Host and need no tenant: they are exempt from the HTTPS redirect and from
            // tenant resolution, which on a one-site installation registers an unknown host as an alias (HealthEndpoints.Matcher).
            var isHealthEndpoint = HealthEndpoints.Matcher(configuration);

            var installation = configuration.GetSection("Installation");
            var useHttpsRedirection = installation.GetValue<bool>("UseHttpsRedirection");
            if (useHttpsRedirection)
            {
                app.UseWhen(context => !isHealthEndpoint(context.Request.Path), https => https.UseHttpsRedirection());
            }

            // Opt-in hooks that must run EARLIER than IServerStartup.Configure (which stays where it was, below). Each
            // stage is invoked at its point; see ServerPipelineStage.
            var pipelineHooks = DiscoverPipelineStartups();
            app.UseServerPipelineStage(ServerPipelineStage.BeforeStaticFiles, pipelineHooks, environment);

            // BL-246: a tenant's files (wwwroot/Content/Tenants) are never served as static files - they go through the
            // checked file route, where their folder's permissions apply (TenantContentRedirect, after tenant resolution).
            app.UseWhen(context => !Stomme.Infrastructure.Files.TenantContentRedirect.IsTenantContent(context.Request.Path), staticFiles => staticFiles.UseStaticFiles(new StaticFileOptions
            {
                // OnPrepareResponseAsync, not OnPrepareResponse: the sync hook is an Action<> and the CORS
                // policy lookup below is async, so the sync form could only be satisfied by blocking (D32).
                // ASP.NET Core has offered the async hook since .NET 8, so this is a real await, not a
                // boundary we have to keep.
                OnPrepareResponseAsync = async (ctx) =>
                {
                    // Blazor framework assets get an explicit, per-file policy that OVERRIDES the site-wide knob
                    // below — immutable for the fingerprinted assemblies, no-cache for the stable-named loaders.
                    // Serving these with no Cache-Control at all let a CDN invent one and pin a stale loader
                    // across a deploy, which stopped the WASM app booting for hours (BL-62; see
                    // FrameworkAssetCaching for the measurement). Assigned, not appended: the two policies are
                    // mutually exclusive and a duplicated header is its own bug.
                    var frameworkCacheControl = FrameworkAssetCaching.Resolve(ctx.Context.Request.Path.Value);
                    if (frameworkCacheControl != null)
                    {
                        ctx.Context.Response.Headers[HeaderNames.CacheControl] = frameworkCacheControl;
                    }
                    else
                    {
                        // static asset caching
                        var cachecontrol = configuration.GetSection("Caching:Cache-Control");
                        if (!string.IsNullOrEmpty(cachecontrol.Value))
                        {
                            ctx.Context.Response.Headers.Append(HeaderNames.CacheControl, cachecontrol.Value);
                        }
                    }
                    // CORS headers for .NET MAUI clients
                    var policy = await corsPolicyProvider.GetPolicyAsync(ctx.Context, Constants.MauiCorsPolicy);
                    corsService.ApplyResult(corsService.EvaluatePolicy(ctx.Context, policy), ctx.Context.Response);
                }
            }));
            app.UseExceptionMiddleWare();
            app.UseServerPipelineStage(ServerPipelineStage.BeforeTenantResolution, pipelineHooks, environment);
            app.UseWhen(context => !isHealthEndpoint(context.Request.Path), tenants => tenants.UseTenantResolution());
            Stomme.Infrastructure.Files.TenantContentRedirect.UseTenantContentRedirect(app);
            // BL-55: ensure the resolved tenant's dedicated OpenIddict store EXISTS before authentication —
            // OpenIddict validates token requests against its store inside the auth stage. No-op unless
            // OpenIddict:Enabled.
            app.UseOpenIddictStoreProvisioning(configuration);
            app.UseServerPipelineStage(ServerPipelineStage.BeforeJwt, pipelineHooks, environment);
            app.UseJwtAuthorization();
            app.UseServerPipelineStage(ServerPipelineStage.BeforeRouting, pipelineHooks, environment);
            app.UseRouting();
            app.UseCors();
            app.UseOutputCache();
            app.UseAuthentication();
            // BL-196: api/* accepts this host's access tokens. After authentication, which creates OpenIddict's validation
            // context (measured: before it, every token failed with "error occurred while retrieving the OpenIddict
            // validation context"), and before authorization. No-op unless OpenIddict:Enabled.
            app.UseOpenIddictApiBearer(configuration);
            app.UseAuthorization();
            // BL-121: the ONE rate-limiter middleware (after authentication, so a policy may partition by caller; before
            // antiforgery, so a refused attempt costs nothing more). Endpoints opt in by policy - see AccountRateLimiting.
            app.UseRateLimiter();
            // gRPC-Web request translation (opt-in, D10) — after auth so services see the authenticated principal
            app.UseStommeTransport(configuration);
            app.UseNotFoundResponse();
            app.UseAntiforgery();

            // execute any IServerStartup logic
            app.ConfigureStommeAssemblies(environment);

            if (configuration.GetSection("UseSwagger").Value == "true")
            {
                app.UseEndpoints(endpoints => endpoints.MapOpenApi());
            }

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapRazorPages();

                // BL-210: an api/ request no endpoint takes - a route a switched-off feature's controller declines at selection
                // (its ActionMethodSelectorAttribute), or one that does not exist - ends here as a bare 404. It fell through to
                // the page fallback below, which answered a POST's JSON body 400 ("The request has an incorrect Content-type")
                // and a GET with the site's page. Every api/ route this host maps has a literal segment after api/
                // (ControllerRoutes.ApiRoute; a site in a folder is rewritten to it by TenantMiddleware), so each outranks this
                // catch-all, and a declined one leaves it the best candidate.
                endpoints.Map("api/{**path}", context =>
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return Task.CompletedTask;
                }).WithDisplayName("api/ not found (BL-210)");

                // D61 spike: per-tenant OpenIddict authority endpoints (/connect/token, /connect/whoami).
                // No-op unless OpenIddict:Enabled=true.
                endpoints.MapStommeOpenIddict(configuration);

                // D104: anonymous liveness and readiness. Neither executes a probe - readiness reports the last
                // completed job tick from memory - so an unauthenticated caller cannot drive database round-trips,
                // cache writes or SMTP connects by refreshing.
                endpoints.MapStommeHealthEndpoints(configuration);
            });

            // opt-in transport endpoints (gRPC-Web module services + SignalR hubs) — no-op unless enabled (D10)
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapStommeTransport(configuration);
            });

            app.UseEndpoints(endpoints =>
            {
                // AddInteractiveWebAssemblyRenderMode logs a one-time startup warning
                // (Microsoft.AspNetCore.Components.WebAssembly.Server.WebAssemblyComponentsEndpointOptions[1])
                // recommending MapStaticAssets over the classic app.UseStaticFiles above. That is EXPECTED and
                // intentional here: the classic UseStaticFiles carries a load-bearing async OnPrepareResponseAsync
                // hook for the .NET MAUI CORS policy + Cache-Control headers (D32), which MapStaticAssets' static
                // manifest cannot host. Migrating would re-home the MAUI CORS logic and needs a driven MAUI
                // client to verify — deliberately NOT done. The warning is silenced at its logger category under
                // Logging:LogLevel in appsettings.json instead of reordering the pipeline.
                endpoints.MapRazorComponents<App>()
                    .AddInteractiveServerRenderMode()
                    .AddInteractiveWebAssemblyRenderMode()
                    .AddAdditionalAssemblies(typeof(SiteRouter).Assembly);
            });

            // simulate the fallback routing approach of traditional Blazor - allowing the custom SiteRouter to handle all routing concerns
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapFallback();
            });

            // create a global sync event to identify server application startup
            await sync.AddSyncEventAsync(-1, -1, EntityNames.Host, -1, SyncEventActions.Reload);

            return app;
        }

        public static IApplicationBuilder ConfigureStommeAssemblies(this IApplicationBuilder app, IWebHostEnvironment env)
        {
            var startUps = AppDomain.CurrentDomain
                .GetStommeAssemblies()
                .SelectMany(x => x.GetInstances<IServerStartup>());

            foreach (var startup in startUps)
            {
                startup.Configure(app, env);
            }
            
            return app;
        }

        /// <summary>
        /// Every <see cref="IServerPipelineStartup"/> in the loaded Stomme assemblies, in the order they run: by stage, then
        /// <see cref="IServerPipelineStartup.Order"/>, then the type's full name (ordinal) - never assembly load order.
        /// </summary>
        public static IReadOnlyList<IServerPipelineStartup> DiscoverPipelineStartups() =>
            OrderPipelineStartups(AppDomain.CurrentDomain
                .GetStommeAssemblies()
                .SelectMany(x => x.GetInstances<IServerPipelineStartup>()));

        public static IReadOnlyList<IServerPipelineStartup> OrderPipelineStartups(IEnumerable<IServerPipelineStartup> hooks) =>
            hooks
                .OrderBy(hook => hook.Stage)
                .ThenBy(hook => hook.Order)
                .ThenBy(hook => hook.GetType().FullName, StringComparer.Ordinal)
                .ToList();

        /// <summary>Calls <see cref="IServerPipelineStartup.Configure"/> on each hook of <paramref name="stage"/>, in order.</summary>
        public static IApplicationBuilder UseServerPipelineStage(this IApplicationBuilder app, ServerPipelineStage stage, IEnumerable<IServerPipelineStartup> hooks, IWebHostEnvironment env)
        {
            foreach (var hook in OrderPipelineStartups(hooks.Where(hook => hook.Stage == stage)))
            {
                hook.Configure(app, env);
            }

            return app;
        }

        public static IApplicationBuilder UseStommeLocalization(this IApplicationBuilder app)
        {
            var localizationManager = app.ApplicationServices.GetService<ILocalizationManager>();
            var defaultCulture = localizationManager.GetDefaultCulture();
            var supportedCultures = localizationManager.GetSupportedCultures();

            app.UseRequestLocalization(options =>
            {
                options.SetDefaultCulture(defaultCulture)
                    .AddSupportedCultures(supportedCultures)
                    .AddSupportedUICultures(supportedCultures);

                foreach(var culture in options.SupportedCultures)
                {
                    if (culture.TextInfo.IsRightToLeft)
                    {
                        RightToLeftCulture.ResolveFormat(culture);
                    }
                }
            });

            return app;
        }

        public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder builder)
          => builder.UseMiddleware<TenantMiddleware>();

        public static IApplicationBuilder UseJwtAuthorization(this IApplicationBuilder builder)
          => builder.UseMiddleware<JwtMiddleware>();

        public static IApplicationBuilder UseExceptionMiddleWare(this IApplicationBuilder builder)
          => builder.UseMiddleware<ExceptionMiddleware>();

        public static IApplicationBuilder UseNotFoundResponse(this IApplicationBuilder app)
        {
            // set the route to be rendered on NavigationManager.NotFound()
            const string notFoundRoute = "/404";
            app.UseStatusCodePagesWithReExecute(notFoundRoute, createScopeForStatusCodePages: true);

            // middleware to determine if status code pages should be skipped
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value ?? string.Empty;
                if (ShouldSkipStatusCodeReExecution(path))
                {
                    var statusCodePagesFeature = context.Features.Get<IStatusCodePagesFeature>();
                    if (statusCodePagesFeature != null)
                    {
                        statusCodePagesFeature.Enabled = false;
                    }
                }

                await next();
            });

            // middleware to rewrite the path for 404 status code responses on sites using subfolders
            app.Use(async (context, next) =>
            {
                var statusCodeReExecuteFeature = context.Features.Get<IStatusCodeReExecuteFeature>();
                if (statusCodeReExecuteFeature != null
                        && context.Response.StatusCode == (int)HttpStatusCode.NotFound
                        && string.Equals(context.Request.Path.Value, notFoundRoute, StringComparison.OrdinalIgnoreCase))
                {
                    var alias = context.GetAlias();
                    if (!string.IsNullOrEmpty(alias?.Path))
                    {
                        var originalPath = context.Request.Path;
                        context.Request.Path = new PathString($"/{alias.Path}{notFoundRoute}");
                        try
                        {
                            await next();
                        }
                        finally
                        {
                            context.Request.Path = originalPath;
                        }
                        return;
                    }
                }

                await next();
            });

            return app;
        }

        static bool ShouldSkipStatusCodeReExecution(string path)
        {
            // skip requests for framework resources, reserved routes, and static files
            return path.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/_content/", StringComparison.OrdinalIgnoreCase) ||
                // D104: the health endpoints must keep their status code and their one-word body.
                //
                // MEASURED 2026-09-20, and it refutes D104 binding item 15: this entry is NOT currently what makes
                // that true. Removing it leaves /readyz answering 503 "not-ready" exactly as before, because
                // UseStatusCodePagesWithReExecute does not re-execute a response that has already WRITTEN A BODY,
                // and both endpoints write one. The entry is kept as belt-and-braces - it is what would keep a
                // future body-less health response (a bare 503, a HEAD request) out of the site''s 404 page - but
                // no test depends on it, and the comment says so rather than implying cover that does not exist.
                Modules.Health.Infrastructure.HealthEndpoints.IsHealthEndpoint(path) ||
                Constants.ReservedRoutes.Any(item => path.Contains("/" + item + "/")) ||
                HasStaticFileExtension(path);
        }

        static bool HasStaticFileExtension(string path)
        {
            return !string.IsNullOrEmpty(Path.GetExtension(path)) && Path.GetExtension(path).Length != 1;
        }
    }
}
