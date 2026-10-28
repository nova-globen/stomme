#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Stomme.Infrastructure.Security
{
    /// <summary>
    /// The framework's outbound connect guard (BL-100 F1, D109 item 14, with D100's SSRF semantics): a request the
    /// SERVER makes to an operator- or user-typed URL must not reach the host's own loopback services, the LAN (the
    /// shared infra tier is RFC1918), or the cloud metadata endpoint.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Denied: loopback (127/8, ::1), RFC1918 (10/8, 172.16/12, 192.168/16), link-local (169.254/16 incl.
    /// 169.254.169.254, fe80::/10), unique-local fc00::/7, the unspecified/"this network" addresses (0/8, ::),
    /// multicast (224/4, ff00::/8), the IPv4 limited broadcast, and the IPv4-mapped IPv6 form of every IPv4 range.
    /// </para>
    /// <para>
    /// <b>The decision is taken where the bytes go.</b> <see cref="ConnectAsync"/> is the
    /// <see cref="SocketsHttpHandler.ConnectCallback"/>: it resolves the host, connects ONLY to permitted addresses,
    /// and re-checks the endpoint the socket actually connected to - so a DNS answer that changes between a check
    /// and the connect (rebinding) cannot slip through. Redirects are off (a 3xx to an internal address would
    /// otherwise be followed), the proxy is off (a proxy would move the check onto the proxy's address), and only
    /// http/https URLs are accepted (<see cref="IsPermittedScheme"/>).
    /// </para>
    /// <para>
    /// <b>ONE guard (BL-132).</b> Every server-side outbound request to a URL somebody typed goes through this class:
    /// the notification webhook channel, the file upload-by-URL path and the MCP client. BL-78 had shipped a
    /// webhook-scoped copy (<c>WebhookSsrfGuard</c>) with the same deny set and the same connect step; it was collapsed
    /// onto this class, and what is left of it is only the webhook's own setting name and wording. A caller's allowlist
    /// is a caller-supplied option (<see cref="AllowedHostsOption"/>), never a second guard. <c>OneOutboundGuardTests</c>
    /// fails the build on a second connect callback anywhere in the server.
    /// </para>
    /// </remarks>
    public static class OutboundConnectGuard
    {
        /// <summary>The request option carrying host names allowed even when they resolve to a denied address.</summary>
        public static readonly HttpRequestOptionsKey<IReadOnlyCollection<string>> AllowedHostsOption = new("Stomme.Outbound.AllowedHosts");

        /// <summary>True when <paramref name="address"/> is in a range a server-side outbound request may not reach.</summary>
        public static bool IsDenied(IPAddress address)
        {
            ArgumentNullException.ThrowIfNull(address);

            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = address.GetAddressBytes();
                return b[0] == 127                              // loopback 127/8
                    || b[0] == 10                               // RFC1918 10/8
                    || (b[0] == 172 && (b[1] & 0xF0) == 16)     // RFC1918 172.16/12
                    || (b[0] == 192 && b[1] == 168)             // RFC1918 192.168/16
                    || (b[0] == 169 && b[1] == 254)             // link-local 169.254/16 (metadata 169.254.169.254)
                    || b[0] == 0                                // "this network" 0/8, incl. 0.0.0.0
                    || (b[0] & 0xF0) == 224                     // multicast 224/4
                    || address.Equals(IPAddress.Broadcast);     // 255.255.255.255
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var b = address.GetAddressBytes();
                return address.Equals(IPAddress.IPv6Loopback)   // ::1
                    || address.Equals(IPAddress.IPv6Any)        // ::
                    || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80)  // link-local fe80::/10
                    || (b[0] & 0xFE) == 0xFC                    // unique local fc00::/7
                    || b[0] == 0xFF;                            // multicast ff00::/8
            }

            return true; // no other address family is a legitimate outbound destination
        }

        /// <summary>
        /// The CONFIGURE-time check, for a URL an operator is about to save: http/https only, and the host (an IP literal
        /// or a resolved name) must not map to ANY denied address unless it is allowlisted. Returns the refusal, or null
        /// when the destination is permitted. The send is still checked at connect (<see cref="ConnectAsync"/>) - a name
        /// that resolved to a public address today may resolve to loopback tomorrow.
        /// </summary>
        public static async Task<string?> CheckDestinationAsync(Uri endpoint, IReadOnlyCollection<string> allowedHosts, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(allowedHosts);

            if (!IsPermittedScheme(endpoint))
            {
                return "The URL must be an absolute http or https URL.";
            }

            var host = NormalizeHost(endpoint.Host);
            if (IsAllowlisted(host, allowedHosts))
            {
                return null;
            }

            IPAddress[] addresses;
            try
            {
                addresses = await ResolveAsync(host, cancellationToken);
            }
            catch (SocketException)
            {
                return UnresolvedMessage(host);
            }

            if (addresses.Length == 0)
            {
                return UnresolvedMessage(host);
            }

            return addresses.Any(IsDenied) ? DeniedMessage(host) : null;
        }

        /// <summary>True for an absolute http or https URL - the only schemes an outbound fetch may use.</summary>
        public static bool IsPermittedScheme(Uri? uri) =>
            uri != null && uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        /// <summary>The guarded handler: no redirects, no proxy, no cookies, and <see cref="ConnectAsync"/> as the connect step.</summary>
        public static SocketsHttpHandler CreateHandler() => new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = ConnectAsync
        };

        /// <summary>
        /// The connect step. Resolves the host, keeps only permitted addresses (all of them when the host is
        /// allowlisted through <see cref="AllowedHostsOption"/>), connects to those alone, and re-checks the address
        /// the socket actually connected to.
        /// </summary>
        public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
        {
            var host = NormalizeHost(context.DnsEndPoint.Host);
            var allowed = context.InitialRequestMessage.Options.TryGetValue(AllowedHostsOption, out var hosts) ? hosts : Array.Empty<string>();
            var allowlisted = IsAllowlisted(host, allowed);

            IPAddress[] resolved;
            try
            {
                resolved = await ResolveAsync(host, cancellationToken);
            }
            catch (SocketException)
            {
                throw new OutboundDestinationDeniedException(UnresolvedMessage(host));
            }

            var permitted = allowlisted ? resolved : resolved.Where(address => !IsDenied(address)).ToArray();
            if (permitted.Length == 0)
            {
                throw new OutboundDestinationDeniedException(DeniedMessage(host));
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(permitted, context.DnsEndPoint.Port, cancellationToken);
                if (!allowlisted && (socket.RemoteEndPoint is not IPEndPoint connected || IsDenied(connected.Address)))
                {
                    throw new OutboundDestinationDeniedException(DeniedMessage(host));
                }

                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>True when <paramref name="exception"/> (or an inner exception) is this guard's refusal - or the connector contract's own (<see cref="Stomme.Connectivity.ConnectorEgressDeniedException"/>, BL-96), which a connector throws when the host pinned to nothing.</summary>
        public static bool IsDenial(Exception? exception)
        {
            if (FindDenial(exception) is not null)
            {
                return true;
            }

            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is Stomme.Connectivity.ConnectorEgressDeniedException)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>This guard's refusal inside <paramref name="exception"/> (it arrives wrapped in an <see cref="HttpRequestException"/>), or null.</summary>
        public static OutboundDestinationDeniedException? FindDenial(Exception? exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is OutboundDestinationDeniedException denied)
                {
                    return denied;
                }
            }

            return null;
        }

        private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            IPAddress.TryParse(host, out var literal) ? new[] { literal } : await Dns.GetHostAddressesAsync(host, cancellationToken);

        private static bool IsAllowlisted(string host, IReadOnlyCollection<string> allowedHosts) =>
            allowedHosts.Any(item => string.Equals(NormalizeHost(item), host, StringComparison.OrdinalIgnoreCase));

        private static string NormalizeHost(string? host) => (host ?? string.Empty).Trim().Trim('[', ']');

        private static string UnresolvedMessage(string host) => $"The host '{host}' cannot be resolved.";

        private static string DeniedMessage(string host) =>
            $"The destination '{host}' is not permitted: it resolves to a loopback, private, link-local or otherwise internal address.";
    }

    /// <summary>The connect guard's refusal, raised from the connect callback (surfaced wrapped in an <see cref="HttpRequestException"/>).</summary>
    public sealed class OutboundDestinationDeniedException : IOException
    {
        public OutboundDestinationDeniedException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// The ONE guarded <see cref="HttpClient"/> the file-ingestion guard downloads through (a singleton - its handler
    /// pools sockets). A dedicated type, so no other registration can be substituted for it by container order.
    /// </summary>
    public sealed class FileDownloadHttpClient : IDisposable
    {
        public FileDownloadHttpClient()
        {
            Client = new HttpClient(OutboundConnectGuard.CreateHandler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };
        }

        public HttpClient Client { get; }

        public void Dispose() => Client.Dispose();
    }
}