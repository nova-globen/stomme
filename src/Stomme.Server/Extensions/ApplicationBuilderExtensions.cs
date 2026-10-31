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
            // Validate the entitlement leg POST-BUILD and BEFORE anything starts — a stray or replaced
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
            // tenant resolution, which answers a Host that names no alias with a 404 (HealthEndpoints.Matcher).
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

            // A tenant's files (wwwroot/Content/Tenants) are never served as static files - they go through the
            // checked file route, where their folder's permissions apply (TenantContentRedirect, after tenant resolution).
            app.UseWhen(context => !Stomme.Infrastructure.Files.TenantContentRedirect.IsTenantContent(context.Request.Path), staticFiles => staticFiles.UseStaticFiles(new StaticFileOptions
            {
                // OnPrepareResponseAsync, not OnPrepareResponse: the sync hook is an Action<> and the CORS
                // policy lookup below is async, so the sync form could only be satisfied by blocking.
                // ASP.NET Core has offered the async hook since .NET 8, so this is a real await, not a
                // boundary we have to keep.
                OnPrepareResponseAsync = async (ctx) =>
                {
                    // Blazor framework assets get an explicit, per-file policy that OVERRIDES the site-wide knob
                    // below — immutable for the fingerprinted assemblies, no-cache for the stable-named loaders.
                    // Serving these with no Cache-Control at all let a CDN invent one and pin a stale loader
                    // across a deploy, which stopped the WASM app booting for hours (see
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
            // A folder site's framework cookies are its own (named and scoped to its path) - right after the alias
            // is known and before anything reads or writes a cookie.
            app.UseWhen(context => !isHealthEndpoint(context.Request.Path), tenants => tenants.UseTenantResolution().UseMiddleware<FolderSiteCookieMiddleware>());
            Stomme.Infrastructure.Files.TenantContentRedirect.UseTenantContentRedirect(app);
            // Ensure the resolved tenant's dedicated OpenIddict store EXISTS before authentication —
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
            // Api/* accepts this host's access tokens. After authentication, which creates OpenIddict's validation
            // context (measured: before it, every token failed with "error occurred while retrieving the OpenIddict
            // validation context"), and before authorization. No-op unless OpenIddict:Enabled.
            app.UseOpenIddictApiBearer(configuration);
            app.UseAuthorization();
            // The ONE rate-limiter middleware (after authentication, so a policy may partition by caller; before
            // antiforgery, so a refused attempt costs nothing more). Endpoints opt in by policy - see AccountRateLimiting.
            app.UseRateLimiter();
            // gRPC-Web request translation (opt-in) — after auth so services see the authenticated principal
            app.UseStommeTransport(configuration);
            app.UseNotFoundResponse();
            app.UseAntiforgery();

            // execute any IServerStartup logic
            app.ConfigureStommeAssemblies(environment);

            if (string.Equals(configuration.GetSection("UseSwagger").Value, "true", StringComparison.OrdinalIgnoreCase)) // a JSON true reads "True" (BL-251, A7)
            {
                app.UseEndpoints(endpoints => endpoints.MapOpenApi());
            }

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapRazorPages();

                // Modules' plain HTTP endpoints, on every install (no transport needed), except an assembly a host
                // switched off (module.<id>.enabled=false).
                MapModuleHttpEndpoints(endpoints, configuration);

                // An api/ request no endpoint takes - a route a switched-off feature's controller declines at selection
                // (its ActionMethodSelectorAttribute), or one that does not exist - ends here as a bare 404. It fell through to
                // the page fallback below, which answered a POST's JSON body 400 ("The request has an incorrect Content-type")
                // and a GET with the site's page. Every api/ route this host maps has a literal segment after api/
                // (ControllerRoutes.ApiRoute; a site in a folder is rewritten to it by TenantMiddleware), so each outranks this
                // catch-all, and a declined one leaves it the best candidate.
                endpoints.Map("api/{**path}", context =>
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return Task.CompletedTask;
                }).WithDisplayName("api/ not found");

                // Per-tenant OpenIddict authority endpoints (/connect/token, /connect/whoami).
                // No-op unless OpenIddict:Enabled=true.
                endpoints.MapStommeOpenIddict(configuration);

                // Anonymous liveness and readiness. Neither executes a probe - readiness reports the last
                // completed job tick from memory - so an unauthenticated caller cannot drive database round-trips,
                // cache writes or SMTP connects by refreshing.
                endpoints.MapStommeHealthEndpoints(configuration);
            });

            // opt-in transport endpoints (gRPC-Web module services + SignalR hubs) — no-op unless enabled
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
                // hook for the .NET MAUI CORS policy + Cache-Control headers, which MapStaticAssets' static
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

        /// <summary>Maps every <see cref="IModuleHttpEndpointContributor"/> of a module assembly this host has not switched off.</summary>
        internal static void MapModuleHttpEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints, IConfiguration configuration)
        {
            var switches = Stomme.FeatureManagement.Infrastructure.FeatureConfigurationSources.FromConfiguration(configuration);
            foreach (var assembly in AppDomain.CurrentDomain.GetStommeAssemblies())
            {
                if (!assembly.GetTypes(typeof(IModuleHttpEndpointContributor)).Any() || ModuleFeatureFlags.IsSwitchedOff(switches, assembly))
                {
                    continue;
                }

                foreach (var contributor in assembly.GetInstances<IModuleHttpEndpointContributor>())
                {
                    contributor.MapEndpoints(endpoints);
                }
            }
        }

        static bool ShouldSkipStatusCodeReExecution(string path)
        {
            // skip requests for framework resources, reserved routes, and static files
            return path.StartsWith("/_framework/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/_content/", StringComparison.OrdinalIgnoreCase) ||
                // The health endpoints must keep their status code and their one-word body.
                //
                // MEASURED: this entry is NOT currently what makes
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
