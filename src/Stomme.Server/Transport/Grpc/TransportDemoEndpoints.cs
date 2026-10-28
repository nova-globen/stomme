using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Stomme.Transport.Grpc
{
    /// <summary>
    /// The reference <see cref="IModuleEndpointContributor"/> (brief 04 / D10): maps the
    /// <see cref="TransportDemoGrpcService"/> onto the host with gRPC-Web enabled, exactly as a real module's
    /// contributor would. The transport pipeline discovers this by reflection (no per-module wiring in the
    /// composition root) and calls <see cref="MapEndpoints"/> whenever at least one transport is enabled;
    /// the gRPC gate below is this contributor's own (BL-53) — <c>MapGrpcService</c> requires the services
    /// <c>AddGrpc</c> registers, which only exist when gRPC is on.
    /// </summary>
    /// <remarks>
    /// The demo endpoint is intentionally left <b>unauthenticated</b> so the transport can be smoke-tested
    /// end-to-end without a seeded identity. A real module adds <c>.RequireAuthorization(policy)</c> here and
    /// enforces per-method permissions inside the service (mirroring the predecessor platform's Pipelines surface).
    /// </remarks>
    public sealed class TransportDemoEndpoints : IModuleEndpointContributor
    {
        public void MapEndpoints(IEndpointRouteBuilder endpoints, TransportOptions transports)
        {
            if (transports.Grpc.Enabled)
            {
                endpoints.MapGrpcService<TransportDemoGrpcService>().EnableGrpcWeb();
            }
        }
    }
}
