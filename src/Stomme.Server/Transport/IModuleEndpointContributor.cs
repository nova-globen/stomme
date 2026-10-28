using Microsoft.AspNetCore.Routing;

namespace Stomme.Transport
{
    /// <summary>
    /// Optional module capability (brief 04 / D10, generalized in BL-53): a module implements this to
    /// contribute its own endpoints — its generated gRPC-Web services and/or SignalR hubs — during host
    /// startup, so the composition root maps nothing per-module. The transport pipeline discovers every
    /// implementor across the loaded Stomme assemblies (the same reflection seam that drives
    /// <c>IServerStartup</c>) and calls <see cref="MapEndpoints"/> on each whenever at least one transport
    /// is enabled, passing the effective <see cref="TransportOptions"/> so the contributor maps each
    /// endpoint only when its transport is on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Adopted from the predecessor platform's <c>IModuleEndpointContributor</c>. Kept off the core <c>IModule</c> contract
    /// because it needs <see cref="IEndpointRouteBuilder"/> (a host-only dependency); service registration
    /// still belongs in the module's <c>IServerStartup.ConfigureServices</c> — only endpoint mapping lives
    /// here. Implementations must have a public parameterless constructor (the discovery seam activates them
    /// via <c>Activator</c>, like <c>IServerStartup</c>).
    /// </para>
    /// <para>
    /// History: this seam originally lived in <c>Stomme.Transport.Grpc</c> and the pipeline invoked it
    /// only inside the gRPC gate, so a module's SignalR hub was silently never mapped on a SignalR-only
    /// install (BL-53a — masked because every transport test enabled both flags). The contract below is the
    /// one the original doc comment already promised: the loop runs when ANY transport is enabled, and the
    /// per-transport branch belongs to the contributor, which now has the options in hand to make it.
    /// </para>
    /// </remarks>
    public interface IModuleEndpointContributor
    {
        /// <summary>
        /// Maps the module's endpoints, branching per transport on <paramref name="transports"/> — e.g.
        /// <c>if (transports.Grpc.Enabled) endpoints.MapGrpcService&lt;TService&gt;().EnableGrpcWeb().RequireAuthorization();</c>
        /// and
        /// <c>if (transports.SignalR.Enabled) endpoints.MapHub&lt;THub&gt;(route).RequireAuthorization(policy);</c>.
        /// Called once whenever at least one transport is enabled; a contributor MUST gate each mapping on
        /// its transport's flag — the corresponding services (<c>AddGrpc</c>/<c>AddSignalR</c>) are only
        /// registered when that transport is on, so an ungated <c>MapGrpcService</c>/<c>MapHub</c> throws on
        /// an install that disabled it.
        /// </summary>
        void MapEndpoints(IEndpointRouteBuilder endpoints, TransportOptions transports);
    }
}
