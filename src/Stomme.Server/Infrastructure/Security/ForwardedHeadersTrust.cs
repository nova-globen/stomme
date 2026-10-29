using System;
using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;

namespace Stomme.Infrastructure.Security
{
    /// <summary>
    /// Whose <c>X-Forwarded-For</c>, <c>-Proto</c> and <c>-Host</c> the host believes. It used to clear the known
    /// proxies and networks, so it believed ANY caller: driven on a directly reached app, a request picked its own issuer
    /// for the OpenID Connect provider, its own scheme past the token endpoint's https-only check, its own client address
    /// past the sign-in rate limiter, and - on a one-site install - a new alias row per request. Now only the proxies a
    /// deployment names are believed, and by default the addresses a reverse proxy in front of the app has: loopback and
    /// the private networks (a container network, the LAN). A caller from any other address is taken as it connected.
    /// </summary>
    public static class ForwardedHeadersTrust
    {
        public const string KnownProxiesKey = "ForwardedHeaders:KnownProxies";
        public const string KnownNetworksKey = "ForwardedHeaders:KnownNetworks";

        // Loopback and the private ranges (RFC 1918, RFC 4193). The middleware maps an IPv4-mapped IPv6 address, as a
        // dual-stack socket reports one, to IPv4 before it compares (measured: the mapped forms added nothing).
        private static readonly string[] DefaultNetworks = { "127.0.0.0/8", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "::1/128", "fc00::/7" };

        public static void Configure(ForwardedHeadersOptions options, IConfiguration configuration)
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
            // Walk the chain from the right past every trusted proxy to the first address that is not one - the client.
            // A deployment behind Cloudflare has two hops (cloudflared, the edge), and the default of one took cloudflared's
            // address for every visitor, so the sign-in rate limiter saw one client. Entries left of the first untrusted one
            // are the caller's own and are never read.
            options.ForwardLimit = null;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();

            var proxies = Values(configuration, KnownProxiesKey);
            var networks = Values(configuration, KnownNetworksKey);
            foreach (var proxy in proxies)
            {
                options.KnownProxies.Add(IPAddress.TryParse(proxy, out var address) ? address : throw new InvalidOperationException($"'{proxy}' in {KnownProxiesKey} is not an IP address."));
            }

            foreach (var network in proxies.Length == 0 && networks.Length == 0 ? DefaultNetworks : networks)
            {
                options.KnownIPNetworks.Add(System.Net.IPNetwork.TryParse(network, out var parsed) ? parsed : throw new InvalidOperationException($"'{network}' in {KnownNetworksKey} is not a network (address/prefix)."));
            }
        }

        // A list as an array section ("KnownProxies": ["10.0.0.5"]) or as one comma-separated value (an environment variable).
        private static string[] Values(IConfiguration configuration, string key)
        {
            var section = configuration.GetSection(key);
            var items = section.GetChildren().Select(child => child.Value).ToList();
            if (!string.IsNullOrWhiteSpace(section.Value))
            {
                items.AddRange(section.Value.Split(','));
            }

            return items.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!.Trim()).ToArray();
        }
    }
}
