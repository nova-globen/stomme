using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stomme.Infrastructure.Security;
using Stomme.Transport;
using Stomme.Transport.Grpc;
using Stomme.Transport.Realtime;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Composition-root registration for Stomme's two opt-in transports. Both are additive
    /// and gated by the <c>Transport</c> configuration section: with neither enabled this method registers
    /// nothing beyond the shared <see cref="TransportOptions"/> snapshot, so the framework builds and runs
    /// exactly as before. Endpoint mapping (gRPC-Web services + hubs) is a separate step —
    /// <c>MapStommeTransport</c> in the application-builder extensions.
    /// </summary>
    public static class TransportServiceCollectionExtensions
    {
        public static IServiceCollection AddStommeTransport(this IServiceCollection services, IConfiguration configuration)
        {
            var options = TransportOptions.Read(configuration);
            services.AddSingleton(options);

            if (options.Grpc.Enabled)
            {
                // The boundary interceptor maps the framework's permission/validation/not-found failures to
                // gRPC status codes for every service, so each module service stays thin (brief 04 Phase 1).
                services.AddGrpc(grpc => grpc.Interceptors.Add<GrpcExceptionInterceptor>());
            }

            if (options.SignalR.Enabled)
            {
                services.AddSignalR();
                services.AddSingleton<RealtimeConnectionCounter>();
                // The reference push adapter (brief 04 Phase 2): the transport-agnostic INotificationRealtimePush
                // port a producing feature depends on, backed by NotificationHub. Absent when SignalR is off.
                services.AddSingleton<INotificationRealtimePush, NotificationRealtimePush>();
                // The health page's live update: the state word pushed to one site's group. TryAdd, so a host (or a
                // drive) that registered its own port first keeps it.
                services.TryAddSingleton<IHealthRealtimePush, HealthRealtimePush>();

                // Each platform hub is mapped with RequireAuthorization(policy); register the named policies here
                // so the WebSocket connection is authorized with the same identity as the rest of the host. A null
                // permission means "any authenticated user"; a real permission maps to Stomme's permission gate.
                services.AddAuthorization(auth =>
                {
                    foreach (var (name, permission) in HubPolicies.All)
                    {
                        auth.AddPolicy(name, policy => ConfigureHubPolicy(policy, permission));
                    }
                });
            }

            return services;
        }

        /// <summary>
        /// Builds ONE hub policy: an authenticated user, plus - when the hub declares a permission - the
        /// permission-key requirement.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It replaces <c>policy.RequireClaim("permission", permission)</c>, which was INERT: Stomme issues
        /// no <c>permission</c> claim anywhere (measured - the claims a principal carries are name, id, role
        /// and the site key), so that requirement could never be satisfied and a hub declaring a permission
        /// would have rejected EVERY connection, administrators included. The same rows the mediator's
        /// <c>[Requires]</c> and a controller's <c>[RequiresPermissionKey]</c> consult now decide the hub
        /// connection too.
        /// </para>
        /// <para>
        /// A null/empty permission still means "any authenticated user" - what the notification hub ships with. The
        /// health hub declares <c>health:view</c> since it began carrying the health page's live update.
        /// </para>
        /// <para>
        /// Public so the hub policy can be driven without standing up a SignalR host (the replaced line was
        /// a lambda inside a registration callback, i.e. untestable in place).
        /// </para>
        /// </remarks>
        public static void ConfigureHubPolicy(AuthorizationPolicyBuilder policy, string permission)
        {
            policy.RequireAuthenticatedUser();
            if (!string.IsNullOrEmpty(permission))
            {
                policy.AddRequirements(new PermissionKeyRequirement(permission));
            }
        }
    }
}
