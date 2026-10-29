using Microsoft.Extensions.Configuration;

namespace Stomme.Transport
{
    /// <summary>
    /// Opt-in switches for Stomme's two additive transports, bound from the
    /// <c>Transport</c> section of configuration. Both default to <b>off</b> — the framework must build
    /// and run with neither enabled — so a deployment turns on only what it needs:
    /// <code>
    /// "Transport": { "Grpc": { "Enabled": true }, "SignalR": { "Enabled": true } }
    /// </code>
    /// </summary>
    public sealed class TransportOptions
    {
        /// <summary>The configuration section these options bind from.</summary>
        public const string SectionName = "Transport";

        public GrpcTransportOptions Grpc { get; set; } = new();

        public SignalRTransportOptions SignalR { get; set; } = new();

        /// <summary>Reads the transport options from configuration without requiring the options pipeline
        /// (the composition root runs before <c>IOptions</c> is available).</summary>
        public static TransportOptions Read(IConfiguration configuration)
        {
            var options = new TransportOptions();
            configuration.GetSection(SectionName).Bind(options);
            return options;
        }
    }

    public sealed class GrpcTransportOptions
    {
        /// <summary>When true, <c>AddGrpc</c> + the boundary interceptor are registered and gRPC-Web +
        /// module endpoint contributors are mapped. Default false.</summary>
        public bool Enabled { get; set; }
    }

    public sealed class SignalRTransportOptions
    {
        /// <summary>When true, SignalR + the platform hubs and their authorization policies are registered
        /// and mapped. Default false.</summary>
        public bool Enabled { get; set; }
    }
}
