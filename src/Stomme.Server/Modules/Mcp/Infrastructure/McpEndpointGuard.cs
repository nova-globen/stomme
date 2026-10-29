#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.Configuration;

namespace Stomme.Modules.Mcp.Infrastructure
{
    /// <summary>
    /// The guard a refresh dials an EXTERNAL MCP server through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The decision is taken at CONNECT time, against the address the socket actually reaches</b> - not against a
    /// URL, and not against a DNS answer read earlier. A pre-flight look-up is open to a time-of-check/time-of-use race: the transport
    /// resolves again when it dials, so a name that answered a public address during the check can answer a private one
    /// a moment later. The mechanism is <see cref="OutboundConnectGuard"/>, the framework's ONE outbound guard
    /// (its deny set): loopback, RFC1918, link-local including 169.254.169.254, unique-local
    /// fc00::/7, the unspecified and multicast ranges, and the IPv4-mapped IPv6 form of each. Redirects are off - a 302
    /// to an internal address would otherwise be followed past the check - and so is the proxy, which would move the
    /// check onto the proxy's address.
    /// </para>
    /// <para>
    /// <b>This file adds NO address logic of its own.</b> MCP is the third caller of that guard (after the webhook
    /// channel and the upload-by-URL path), and a third copy of a deny set is a third place for one range to be
    /// forgotten. What is MCP's own is the ALLOWLIST: the hosts an
    /// operator has said this install may dial anyway, from <c>Mcp:Client:HostAllowlist</c> and, per site, the setting
    /// <see cref="AllowedHostsSettingName"/>. The two are UNIONED - an install-wide entry and a site's own entry are
    /// both an operator's statement, and neither should silently cancel the other.
    /// </para>
    /// <para>
    /// <b>Why the allowlist travels as a DelegatingHandler.</b> The guard reads it from the request's options, and the
    /// requests are built by the MCP SDK's transport, which this code never sees. <see cref="AllowedHostsHandler"/>
    /// stamps every outgoing request on its way through. The alternative - a connect callback closing over the list -
    /// would be a second guard with the same deny set, which is the thing this file refuses to become.
    /// </para>
    /// <para>
    /// <b>A client per refresh, not a pooled one.</b> A refresh is an explicit, rare, <c>mcp:manage</c> action, and the
    /// allowlist is per SITE: one pooled client would either share one site's allowlist with every site or need the
    /// list to be mutable while sockets are in flight. The connections are not worth that.
    /// </para>
    /// </remarks>
    public interface IMcpEndpointGuard
    {
        /// <summary>
        /// An <see cref="HttpClient"/> that may reach only permitted addresses, plus the hosts this site has
        /// allowlisted. The caller OWNS it and disposes it.
        /// </summary>
        Task<HttpClient> CreateGuardedClientAsync(int siteId, CancellationToken ct = default);

        /// <summary>True when <paramref name="exception"/> (or an inner one) is the guard refusing a destination.</summary>
        bool IsDenial(Exception? exception);
    }

    public sealed class McpEndpointGuard : IMcpEndpointGuard
    {
        /// <summary>The per-site setting holding extra hosts this site may dial: comma or newline separated.</summary>
        public const string AllowedHostsSettingName = "McpClientAllowedHosts";

        /// <summary>The install-wide allowlist, from configuration.</summary>
        public const string AllowedHostsConfigurationKey = "Mcp:Client:HostAllowlist";

        private static readonly char[] Separators = [',', ';', '\n', '\r'];

        private readonly IConfiguration? _configuration;
        private readonly ISettingRepository _settings;

        public McpEndpointGuard(ISettingRepository settings, IConfigurationRoot? configuration = null)
        {
            _settings = settings;
            _configuration = configuration;
        }

        public async Task<HttpClient> CreateGuardedClientAsync(int siteId, CancellationToken ct = default)
        {
            var allowed = await AllowedHostsAsync(siteId);
            var handler = new AllowedHostsHandler(allowed) { InnerHandler = OutboundConnectGuard.CreateHandler() };
            return new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        }

        public bool IsDenial(Exception? exception) => OutboundConnectGuard.IsDenial(exception);

        /// <summary>The union of the install-wide list and this site's own, de-duplicated, ignoring case.</summary>
        public async Task<IReadOnlyCollection<string>> AllowedHostsAsync(int siteId)
        {
            var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var host in Configured())
            {
                hosts.Add(host);
            }

            var setting = await _settings.GetSettingAsync(EntityNames.Site, siteId, AllowedHostsSettingName);
            foreach (var host in Split(setting?.SettingValue))
            {
                hosts.Add(host);
            }

            return hosts;
        }

        private IEnumerable<string> Configured()
        {
            var section = _configuration?.GetSection(AllowedHostsConfigurationKey);
            if (section is null || !section.Exists())
            {
                return Array.Empty<string>();
            }

            // An array of entries, or one delimited string - an operator writing either means the same thing.
            var children = section.GetChildren().Select(child => child.Value).ToList();
            return children.Count != 0
                ? children.SelectMany(Split)
                : Split(section.Value);
        }

        private static IEnumerable<string> Split(string? value) =>
            (value ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>Puts this site's allowlist on every request, where <see cref="OutboundConnectGuard"/> reads it.</summary>
        private sealed class AllowedHostsHandler(IReadOnlyCollection<string> allowedHosts) : DelegatingHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                request.Options.Set(OutboundConnectGuard.AllowedHostsOption, allowedHosts);
                return base.SendAsync(request, cancellationToken);
            }
        }
    }
}
