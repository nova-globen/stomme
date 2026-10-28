using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// BL-243 - the host believes <c>X-Forwarded-*</c> only from a proxy. Found by BL-239's validation, driven on a directly
    /// reached app: the known proxies were cleared, so any caller chose the OpenID Connect issuer (X-Forwarded-Host), the
    /// scheme past the token endpoint's https-only check (X-Forwarded-Proto), its client address past the sign-in rate
    /// limiter (X-Forwarded-For) and, on a one-site install, a new alias row per request. Each case runs ASP.NET Core's own
    /// middleware with the options the host builds.
    /// </summary>
    public sealed class ForwardedHeadersTrustTests
    {
        private static async Task<(string Host, string Scheme, string Client)> ForwardedAsync(string connectedFrom, Dictionary<string, string?>? settings = null)
        {
            var options = new ForwardedHeadersOptions();
            ForwardedHeadersTrust.Configure(options, new ConfigurationBuilder().AddInMemoryCollection(settings ?? new()).Build());
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse(connectedFrom);
            context.Request.Host = new HostString("app.example");
            context.Request.Scheme = "http";
            context.Request.Headers["X-Forwarded-Host"] = "spoofed.invalid";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            context.Request.Headers["X-Forwarded-For"] = "198.51.100.7";
            var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options));
            await middleware.Invoke(context);
            return (context.Request.Host.Value!, context.Request.Scheme, context.Connection.RemoteIpAddress!.ToString());
        }

        [Theory]
        [InlineData("203.0.113.5")]
        [InlineData("2001:db8::5")]
        [InlineData("::ffff:203.0.113.5")]
        public async Task A_caller_from_a_public_address_is_taken_as_it_connected(string caller) =>
            Assert.Equal(("app.example", "http", IPAddress.Parse(caller).ToString()), await ForwardedAsync(caller));

        [Theory]
        [InlineData("127.0.0.1")]
        [InlineData("::1")]
        [InlineData("172.18.0.5")]
        [InlineData("10.1.2.3")]
        [InlineData("192.168.10.20")]
        [InlineData("::ffff:172.18.0.5")]
        public async Task A_reverse_proxy_on_loopback_or_a_private_network_is_believed(string proxy) =>
            Assert.Equal(("spoofed.invalid", "https", "198.51.100.7"), await ForwardedAsync(proxy));

        [Fact]
        public async Task A_deployment_that_names_its_proxies_believes_only_those()
        {
            var named = new Dictionary<string, string?> { [ForwardedHeadersTrust.KnownProxiesKey + ":0"] = "203.0.113.9" };
            Assert.Equal(("spoofed.invalid", "https", "198.51.100.7"), await ForwardedAsync("203.0.113.9", named));
            Assert.Equal(("app.example", "http", "172.18.0.5"), await ForwardedAsync("172.18.0.5", named)); // the defaults no longer apply

            var network = new Dictionary<string, string?> { [ForwardedHeadersTrust.KnownNetworksKey] = "203.0.113.0/24, 198.18.0.0/15" };
            Assert.Equal(("spoofed.invalid", "https", "198.51.100.7"), await ForwardedAsync("203.0.113.200", network));
        }

        // The chain behind Cloudflare, as the edge describes it: the client's own X-Forwarded-For (anything), Cloudflare's
        // append of the real client, cloudflared's private address; the app connected from the edge, also private.
        [Fact]
        public async Task Behind_two_private_hops_the_client_is_the_first_address_that_is_not_a_proxy()
        {
            var options = new ForwardedHeadersOptions();
            ForwardedHeadersTrust.Configure(options, new ConfigurationBuilder().Build());
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse("172.18.0.2");
            context.Request.Host = new HostString("app.example");
            context.Request.Scheme = "http";
            context.Request.Headers["X-Forwarded-For"] = "6.6.6.6, 198.51.100.7, 172.18.0.9";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            context.Request.Headers["X-Forwarded-Host"] = "uat.example";
            await new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance, Options.Create(options)).Invoke(context);
            Assert.Equal(("uat.example", "https", "198.51.100.7"), (context.Request.Host.Value!, context.Request.Scheme, context.Connection.RemoteIpAddress!.ToString()));
        }

        [Fact]
        public void A_value_that_is_not_an_address_stops_the_host_with_its_name() =>
            Assert.Contains("ForwardedHeaders:KnownProxies", Assert.Throws<System.InvalidOperationException>(() => ForwardedHeadersTrust.Configure(new ForwardedHeadersOptions(), new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [ForwardedHeadersTrust.KnownProxiesKey] = "proxy.example" }).Build())).Message);
    }
}
