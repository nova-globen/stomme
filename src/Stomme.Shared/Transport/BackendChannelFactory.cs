using System;
using Grpc.Core;

namespace Stomme.Transport
{
    /// <summary>
    /// Supplies gRPC channels to the backend origin for typed transport clients (brief 04 / D10). One of
    /// these is registered per runtime and resolved from DI wherever a module needs a channel, so a module
    /// never constructs the transport itself and the render-mode-specific wiring lives in exactly one place:
    /// <list type="bullet">
    /// <item>On a WASM / MAUI client the shell registers a factory that creates a <b>gRPC-Web</b> channel
    /// (over the browser's fetch stack) to the server origin.</item>
    /// <item>On the server (interactive Server / Static SSR / the host itself) the factory creates a plain
    /// HTTP/2 <c>GrpcChannel</c> to the local loopback endpoint — no gRPC-Web needed in-process.</item>
    /// </list>
    /// This is the deliberate divergence from the predecessor platform's ADR 0016, which welds a single WASM shell to a
    /// gRPC-Web-only egress: Stomme is multi-render-mode, so the channel transport is chosen by runtime.
    /// </summary>
    public sealed class BackendChannelFactory
    {
        private readonly Func<ChannelBase> _channelFactory;

        /// <param name="origin">The backend authority (scheme + host + port) the client talks to.</param>
        /// <param name="channelFactory">
        /// Creates a new channel to the backend. Owned by the shell/host so the transport and its network
        /// egress live in one reviewed place, not scattered across module clients.
        /// </param>
        public BackendChannelFactory(Uri origin, Func<ChannelBase> channelFactory)
        {
            Origin = origin ?? throw new ArgumentNullException(nameof(origin));
            _channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
        }

        /// <summary>The backend authority; clients also use it to resolve non-gRPC endpoints (e.g. SignalR hubs).</summary>
        public Uri Origin { get; }

        /// <summary>Creates a new channel to the backend. The caller owns the returned channel's lifetime.</summary>
        public ChannelBase CreateChannel() => _channelFactory();
    }
}
