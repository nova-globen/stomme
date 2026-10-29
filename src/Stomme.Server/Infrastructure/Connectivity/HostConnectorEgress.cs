#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Infrastructure.Security;
using Stomme.Repository;
using Stomme.Shared;

namespace Stomme.Infrastructure.Connectivity
{
    /// <summary>
    /// The host's egress for connectors: the ONE outbound policy the framework already has,
    /// <see cref="OutboundConnectGuard"/> (BL-132 converged the webhook and upload guards onto it - the "extraction" D110
    /// planned was done there), handed to a connector through <see cref="IConnectorEgress"/>. An HTTP connector gets a
    /// client whose every connection is decided by the guard's connect step (loopback, RFC1918, link-local, ULA, mapped
    /// and metadata addresses refused where the bytes go; redirects off; one attempt with the timeout) with the site's
    /// allowlist carried on every request; a driver connector asks <see cref="ResolveAndPinAsync"/> for the one address it
    /// may dial. A connector never owns a socket policy, and never sees an address the policy refused.
    /// </summary>
    public sealed class HostConnectorEgress(IReadOnlyCollection<string> allowedHosts) : IConnectorEgress
    {
        /// <summary>The site setting naming the private hosts a site's connections may reach (on-LAN databases); Notifications keeps its own key for webhooks.</summary>
        public const string AllowedHostsSettingName = "ConnectivityAllowedHosts";

        public IReadOnlyCollection<string> AllowedHosts { get; } = allowedHosts ?? throw new ArgumentNullException(nameof(allowedHosts));

        public HttpClient CreateHttpClient(TimeSpan timeout)
        {
            var handler = new AllowedHostsHandler(AllowedHosts) { InnerHandler = OutboundConnectGuard.CreateHandler() };
            return new HttpClient(handler, disposeHandler: true) { Timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(30) : timeout };
        }

        /// <summary>
        /// Resolves the host and returns the first address the policy permits (an allowlisted host is permitted as named);
        /// null when every address is refused or nothing resolves. The driver then dials THAT address; a driver that
        /// re-resolves on its own reopens the window between this check and its connect - stated, not solved.
        /// </summary>
        public async Task<IPAddress?> ResolveAndPinAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return null;
            }

            var trimmed = host.Trim().Trim('[', ']');
            var allowlisted = AllowedHosts.Contains(trimmed, StringComparer.OrdinalIgnoreCase);
            IPAddress[] addresses;
            if (IPAddress.TryParse(trimmed, out var literal))
            {
                addresses = new[] { literal };
            }
            else
            {
                try
                {
                    addresses = await Dns.GetHostAddressesAsync(trimmed, cancellationToken);
                }
                catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
                {
                    return null;
                }
            }

            return addresses.FirstOrDefault(address => allowlisted || !OutboundConnectGuard.IsDenied(address));
        }

        /// <summary>
        /// The contract's reach: the address checked against the policy again where the bytes go - a denied address only
        /// for an allowlisted host it is an address of - then a TCP connect to it bounded by the timeout, and closed. libldap, which
        /// System.DirectoryServices.Protocols uses on Linux, connects synchronously with the system's own timeout and no
        /// cancellation reaches it (measured on UAT 2026-10-17: a test to an address that drops packets hung past Cloudflare's
        /// 125 s with a five-second timeout); reached first, such an address fails in the connection's time.
        /// </summary>
        public async Task ReachAsync(IPAddress address, int port, string host, TimeSpan timeout, string service, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(address);
            ArgumentNullException.ThrowIfNull(host);
            if (OutboundConnectGuard.IsDenied(address) && !await IsAllowlistedAsync(host.Trim().Trim('[', ']'), address, cancellationToken))
            {
                throw new ConnectorEgressDeniedException($"{service}: {address} is not an address this site's egress policy permits for '{host}'.");
            }

            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(timeout);
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), bounded.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException($"{service}: no answer from {host}:{port} within {timeout.TotalSeconds:0} seconds.");
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException($"{service}: {host}:{port} refused the connection ({ex.SocketErrorCode}).");
            }
        }

        // An allowlisted host's own address: the literal it names, or one it resolves to - never any address a caller pairs with its name.
        private async Task<bool> IsAllowlistedAsync(string host, IPAddress address, CancellationToken cancellationToken)
        {
            if (!AllowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            if (IPAddress.TryParse(host, out var literal))
            {
                return literal.Equals(address);
            }

            try
            {
                return (await Dns.GetHostAddressesAsync(host, cancellationToken)).Contains(address);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                return false;
            }
        }

        /// <summary>Whether an exception from a guarded client is the policy's refusal (as opposed to the remote's failure).</summary>
        public static bool IsDenial(Exception? exception) => OutboundConnectGuard.IsDenial(exception);

        /// <summary>The contract's question, answered by the guard: a connector never needs the host's types to tell a refusal from a failure.</summary>
        bool IConnectorEgress.IsDenial(Exception exception) => OutboundConnectGuard.IsDenial(exception);

        private sealed class AllowedHostsHandler(IReadOnlyCollection<string> allowedHosts) : DelegatingHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.Options.Set(OutboundConnectGuard.AllowedHostsOption, allowedHosts);
                return base.SendAsync(request, cancellationToken);
            }
        }
    }

    /// <summary>Builds a site's egress from its allowlist setting - the per-site half D110 asks for.</summary>
    public interface IConnectorEgressFactory
    {
        Task<IConnectorEgress> ForSiteAsync(int siteId, CancellationToken cancellationToken = default);
    }

    public sealed class SiteSettingConnectorEgressFactory(ISettingRepository settings) : IConnectorEgressFactory
    {
        public async Task<IConnectorEgress> ForSiteAsync(int siteId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var setting = await settings.GetSettingAsync(EntityNames.Site, siteId, HostConnectorEgress.AllowedHostsSettingName);
            return new HostConnectorEgress(ParseAllowedHosts(setting?.SettingValue));
        }

        public static IReadOnlyCollection<string> ParseAllowedHosts(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? Array.Empty<string>()
                : value.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(host => host.Trim().Trim('[', ']'))
                    .Where(host => host.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }
}
