using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// Where the host's calls to its OWN API go (BL-234). The client's services run on the server too - in a prerender (the
    /// install wizard's, measured) and behind an Interactive Server circuit - and call the API over HTTP at the site's PUBLIC
    /// address, so a host with no egress (a container behind a tunnel, a game app's host, 2026-10-13) failed every such call -
    /// the wizard answered 500 on every request - and a host with egress sent each round trip out through the edge and back.
    /// They go to the host's own plain-HTTP listener instead, carrying the public host and scheme as the edge would, so the
    /// same alias and site answer them.
    /// </summary>
    /// <remarks>
    /// <c>Installation:InternalUrl</c> decides: empty (the default) - the host's own listener, its first plain-HTTP address,
    /// a wildcard one (<c>http://+:8080</c>, <c>http://[::]:8080</c>) as <c>127.0.0.1</c>; a URL - that one, for a host that
    /// cannot tell (behind its own TLS alone, or in front of another listener); <c>public</c> - the public address, as before.
    /// A host with no plain-HTTP listener, or no listener at all (an in-memory test server), keeps the public address.
    /// </remarks>
    public sealed class SelfCallAddress
    {
        public const string Section = "Installation";

        public const string SettingKey = "InternalUrl";

        public const string Public = "public";

        private readonly IServer _server;
        private readonly IConfigurationRoot _config;
        private readonly ILogger<SelfCallAddress> _logger;
        private volatile Box _resolved;
        private volatile bool _said;

        // The host's own configuration root - the one ending with appsettings.override.json, where a deployed install keeps what it is told (BL-162).
        public SelfCallAddress(IServer server, IConfigurationRoot config, ILogger<SelfCallAddress> logger)
        {
            _server = server;
            _config = config;
            _logger = logger;
        }

        /// <summary>The listener the host's own calls go to, or null for the public address.</summary>
        public Uri Resolve()
        {
            if (_resolved is { } known)
            {
                return known.Address;
            }

            var configured = (_config.GetSection(Section)[SettingKey] ?? string.Empty).Trim();
            if (string.Equals(configured, Public, StringComparison.OrdinalIgnoreCase))
            {
                _resolved = new Box(null);
                return null;
            }

            if (configured.Length > 0)
            {
                if (Uri.TryCreate(configured, UriKind.Absolute, out var given) && given.Scheme is "http" or "https" && string.IsNullOrEmpty(given.UserInfo))
                {
                    _resolved = new Box(new Uri(given.GetLeftPart(UriPartial.Authority) + "/"));
                    return _resolved.Address;
                }

                if (!_said)
                {
                    _said = true;
                    _logger.LogError("{Section}:{Key} '{Value}' is not an http or https address - the host's own listener is used for its calls to itself.", Section, SettingKey, configured);
                }
            }

            // The server's addresses are there once it has started; asked again until they are.
            var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
            if (addresses is null || addresses.Count == 0)
            {
                return null;
            }

            _resolved = new Box(Listener(addresses));
            return _resolved.Address;
        }

        /// <summary>The first plain-HTTP address a server listens on, a wildcard one as the IPv4 loopback - or null.</summary>
        public static Uri Listener(System.Collections.Generic.IEnumerable<string> addresses)
        {
            foreach (var address in addresses)
            {
                BindingAddress binding;
                try
                {
                    binding = BindingAddress.Parse(address);
                }
                catch (FormatException)
                {
                    continue;
                }

                if (binding.IsUnixPipe || !string.Equals(binding.Scheme, "http", StringComparison.OrdinalIgnoreCase) || binding.Port <= 0)
                {
                    continue;
                }

                var host = binding.Host is "+" or "*" or "0.0.0.0" or "[::]" or "::" ? "127.0.0.1" : binding.Host;
                return new Uri($"http://{host}:{binding.Port}/");
            }

            return null;
        }

        private sealed record Box(Uri Address);
    }

    /// <summary>
    /// Sends a call the host makes to its own API (BL-234) to its own listener - <see cref="SelfCallAddress"/> - with the
    /// address it was made to kept as the request's host and scheme (<c>Host</c>, <c>X-Forwarded-Proto</c>), so the alias,
    /// the site and the scheme are what they would have been; the caller's own address as <c>X-Forwarded-For</c> when the
    /// call is made in its request. A call already to the host's listener, or to a loopback address, is sent as it is. Only
    /// the clients the client's services use are given it: everything they call is the site's own API.
    /// </summary>
    public sealed class SelfCallHandler : DelegatingHandler
    {
        private readonly SelfCallAddress _address;
        private readonly IHttpContextAccessor _accessor;

        public SelfCallHandler(SelfCallAddress address, IHttpContextAccessor accessor)
        {
            _address = address;
            _accessor = accessor;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var target = request.RequestUri;
            if (target is { IsAbsoluteUri: true } && target.Scheme is "http" or "https" && !target.IsLoopback && _address.Resolve() is { } local
                && !string.Equals(target.Authority, local.Authority, StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Host = target.Authority;
                request.Headers.Remove("X-Forwarded-Proto");
                request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", target.Scheme);
                if (!request.Headers.Contains("X-Forwarded-For") && _accessor.HttpContext?.Connection.RemoteIpAddress is { } caller)
                {
                    request.Headers.TryAddWithoutValidation("X-Forwarded-For", caller.ToString());
                }

                request.RequestUri = new Uri(local, target.PathAndQuery);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
