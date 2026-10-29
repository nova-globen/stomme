using System;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stomme.Transport;
using Stomme.Transport.Grpc;
using Stomme.Transport.Realtime;

namespace Stomme.Extensions
{
    /// <summary>
    /// Pipeline wiring for Stomme's opt-in transports. Split into a middleware step
    /// (<see cref="UseStommeTransport"/>, the gRPC-Web request translation) and an endpoint-mapping step
    /// (<see cref="MapStommeTransport"/>, the module contributors + hubs). Neither maps anything when the
    /// corresponding transport is disabled, so the middleware/endpoint pipeline is unchanged for a deployment
    /// that opts into neither — the one thing that always runs is the transport-requirement check,
    /// which logs (never throws) when a module declares a transport the install has not enabled.
    /// </summary>
    public static class TransportApplicationBuilderExtensions
    {
        /// <summary>
        /// Inserts the gRPC-Web request-translation middleware when gRPC is enabled. Call after
        /// <c>UseAuthentication</c>/<c>UseAuthorization</c> so services see the authenticated principal.
        /// </summary>
        public static IApplicationBuilder UseStommeTransport(this IApplicationBuilder app, IConfigurationRoot configuration)
        {
            var options = TransportOptions.Read(configuration);
            if (options.Grpc.Enabled)
            {
                app.UseGrpcWeb();
            }

            return app;
        }

        /// <summary>
        /// Maps the transport endpoints: every discovered <see cref="IModuleEndpointContributor"/>'s endpoints
        /// (when at least one transport is enabled — each contributor branches per transport on the
        /// <see cref="TransportOptions"/> it receives) and the platform hubs (when SignalR is enabled). Module
        /// contributors are discovered by reflection across the loaded Stomme assemblies — the same seam that
        /// drives <c>IServerStartup</c> — so the composition root maps nothing per-module. Also checks every
        /// scanned module's <see cref="Models.ModuleDefinition.Transports"/> declaration against the enabled
        /// transports and logs an ERROR naming the module and the missing transport — a declared
        /// requirement must fail loudly, not silently vanish the module's endpoints.
        /// </summary>
        public static void MapStommeTransport(this IEndpointRouteBuilder endpoints, IConfigurationRoot configuration)
        {
            var options = TransportOptions.Read(configuration);

            // Loud, attributable failure for a module whose declared transport requirement the install
            // does not satisfy. Runs unconditionally (a module requiring gRPC on an all-transports-off install
            // must still be named), against the DB-free assembly scan so it works pre-install too.
            var logger = endpoints.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(TransportApplicationBuilderExtensions).FullName);
            TransportRequirements.LogUnsatisfied(
                Repository.ModuleDefinitionRepository.GetModuleDefinitionsFromAssemblies(), options, logger);

            // A module assembly switched off on this host (module.<id>.enabled=false) maps no endpoints, and
            // its hosted services were never registered; say so once, by name, so a missing hub is never a mystery.
            var switches = Stomme.FeatureManagement.Infrastructure.FeatureConfigurationSources.FromConfiguration(configuration);
            foreach (var off in endpoints.ServiceProvider.GetServices<Infrastructure.SwitchedOffModuleAssembly>())
            {
                logger.LogInformation("Module assembly {Assembly} is switched off on this host ({Keys}): its hosted services are not started and its endpoints are not mapped.",
                    off.AssemblyName, string.Join(", ", off.SwitchIds.Select(Infrastructure.ModuleFeatureFlags.FeatureKey)));
            }

            if (options.Grpc.Enabled || options.SignalR.Enabled)
            {
                // Endpoint mapping: each effective module maps its own transport endpoints through the
                // loader. The loop runs when ANY transport is enabled — the per-transport branch
                // belongs to the contributor, which receives the effective options (an earlier shape
                // had the loop sit inside the gRPC gate and module SignalR hubs never mapped on a
                // SignalR-only install). The reference TransportDemoEndpoints is one such contributor.
                var contributors = AppDomain.CurrentDomain
                    .GetStommeAssemblies()
                    .Where(assembly => !assembly.GetTypes(typeof(IModuleEndpointContributor)).Any()
                        || !Infrastructure.ModuleFeatureFlags.IsSwitchedOff(switches, assembly))
                    .SelectMany(assembly => assembly.GetInstances<IModuleEndpointContributor>());

                foreach (var contributor in contributors)
                {
                    contributor.MapEndpoints(endpoints, options);
                }
            }

            if (options.SignalR.Enabled)
            {
                // Versioned, authenticated platform hubs. Each is gated by its per-hub authorization policy
                // (registered in AddStommeTransport). Module hubs map themselves via IModuleEndpointContributor.
                endpoints.MapHub<NotificationHub>(HubRoutes.Notification).RequireAuthorization(HubPolicies.Notification);
                endpoints.MapHub<HealthHub>(HubRoutes.Health).RequireAuthorization(HubPolicies.Health);
            }
        }
    }
}
