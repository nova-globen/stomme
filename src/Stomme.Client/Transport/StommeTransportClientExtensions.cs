using System;
using System.Net.Http;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Stomme.Transport;
using Stomme.Transport.V1;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// Client-side (WASM / MAUI) wiring for Stomme's opt-in gRPC transport. This is the
    /// render-mode-specific half of the transport wiring: from a browser the UI cannot open an HTTP/2 gRPC
    /// socket, so it reaches the server's gRPC services over <b>gRPC-Web</b> (the <see cref="GrpcWebHandler"/>
    /// tunnels gRPC frames through the browser's fetch stack). The shell owns the single credentialed egress via
    /// <see cref="BackendChannelFactory"/>; a module resolves the factory (or a typed client) from DI and never
    /// constructs the transport itself.
    /// </summary>
    /// <remarks>
    /// This is <b>not</b> wired into the base client bootstrap — it is opt-in, so the framework's WASM client
    /// runs unchanged when no module uses gRPC. A downstream app or module calls this from its client startup
    /// once the server has gRPC enabled. On a server-rendered circuit the equivalent factory (registered
    /// on the server) makes a plain HTTP/2 loopback channel instead — same abstraction, transport chosen by
    /// runtime.
    /// </remarks>
    public static class StommeTransportClientExtensions
    {
        /// <summary>
        /// Registers the shell-owned gRPC-Web <see cref="BackendChannelFactory"/> and the reference typed client
        /// for the <c>transport.proto</c> surface, targeting <paramref name="origin"/> (the server the WASM app
        /// was served from).
        /// </summary>
        public static IServiceCollection AddStommeGrpcWebClients(this IServiceCollection services, Uri origin)
        {
            ArgumentNullException.ThrowIfNull(origin);

            services.AddSingleton(_ => new BackendChannelFactory(origin, () =>
            {
                // GrpcWebMode.GrpcWeb tunnels gRPC over HTTP/1.1-friendly gRPC-Web; in WASM the inner handler
                // resolves to the browser's fetch-backed handler automatically.
                var handler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, new HttpClientHandler());
                return GrpcChannel.ForAddress(origin, new GrpcChannelOptions { HttpHandler = handler });
            }));

            services.AddScoped(provider =>
            {
                var factory = provider.GetRequiredService<BackendChannelFactory>();
                return new TransportDemo.TransportDemoClient(factory.CreateChannel());
            });

            return services;
        }
    }
}
