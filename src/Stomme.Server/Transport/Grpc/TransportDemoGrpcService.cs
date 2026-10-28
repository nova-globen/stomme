using System;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Stomme.Transport.V1;

namespace Stomme.Transport.Grpc
{
    /// <summary>
    /// The reference gRPC-Web service for Stomme's opt-in typed transport (brief 04 / D10) — the end-to-end
    /// proof that a module can expose a typed surface: the <c>transport.proto</c> contract (in Stomme.Shared)
    /// generates <see cref="TransportDemo.TransportDemoBase"/>, this thin adapter implements it, and
    /// <see cref="TransportDemoEndpoints"/> maps it onto the host through the <see cref="IModuleEndpointContributor"/>
    /// seam. A real module owns the same three pieces. Bad input throws a BCL exception the shared
    /// <see cref="GrpcExceptionInterceptor"/> maps to <see cref="StatusCode.InvalidArgument"/>, so the method
    /// stays thin.
    /// </summary>
    public sealed class TransportDemoGrpcService : TransportDemo.TransportDemoBase
    {
        // Clamp the server-stream so a client can never ask the host to stream unbounded work.
        private const int MaxHeartbeats = 60;
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(200);

        private readonly ILogger<TransportDemoGrpcService> _logger;

        public TransportDemoGrpcService(ILogger<TransportDemoGrpcService> logger)
        {
            _logger = logger;
        }

        /// <summary>Typed unary round-trip: echoes the message back with a server timestamp.</summary>
        public override Task<EchoReply> Echo(EchoRequest request, ServerCallContext context)
        {
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                // Mapped to InvalidArgument by GrpcExceptionInterceptor.
                throw new ArgumentException("A non-empty message is required.", nameof(request));
            }

            return Task.FromResult(new EchoReply
            {
                Message = request.Message,
                ServerTime = DateTimeOffset.UtcNow.ToString("O"),
            });
        }

        /// <summary>
        /// Server-streaming: emits <c>request.Count</c> heartbeats (clamped to <see cref="MaxHeartbeats"/>) then
        /// completes the stream. A client disconnect trips <see cref="ServerCallContext.CancellationToken"/>, so
        /// the loop ends cleanly. This is the typed twin of a producer-driven push (the SignalR route) for cases
        /// where the caller wants a bounded, request-scoped stream over a single RPC.
        /// </summary>
        public override async Task Heartbeat(
            HeartbeatRequest request,
            IServerStreamWriter<HeartbeatReply> responseStream,
            ServerCallContext context)
        {
            var count = Math.Clamp(request.Count <= 0 ? 1 : request.Count, 1, MaxHeartbeats);
            _logger.LogDebug("TransportDemo.Heartbeat streaming {Count} beat(s).", count);

            for (var sequence = 1; sequence <= count; sequence++)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                await responseStream.WriteAsync(new HeartbeatReply
                {
                    Sequence = sequence,
                    ServerTime = DateTimeOffset.UtcNow.ToString("O"),
                });

                if (sequence < count)
                {
                    await Task.Delay(HeartbeatInterval, context.CancellationToken);
                }
            }
        }
    }
}
