#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Stomme.Infrastructure.Connectivity;
using Stomme.Tests.Notifications;
using Xunit;

namespace Stomme.Tests.Connectivity
{
    /// <summary>
    /// The host's connector egress over the framework's ONE outbound guard. In both directions:
    /// a guarded client refuses a loopback receiver at CONNECT (the receiver sees nothing) and a name resolving to loopback
    /// too; an allowlisted loopback receiver receives the request; a redirect is not followed; the resolve-and-pin call
    /// answers null for a refused address, the address for an allowlisted or a public one, and null for a name that does
    /// not resolve. `OneOutboundGuardTests` is the "no parallel policy" gate D110 asked for - this egress defines no deny
    /// set and assigns no connect step of its own.
    /// </summary>
    public sealed class ConnectorEgressTests
    {
        // Generous on purpose: no test here waits for it (a denial is decided before any socket, an allowlisted receiver
        // answers at once), and under the full gate's parallel host-install load a denial once took longer than 10 seconds
        // to surface (measured 2026-09-27: 18 s in the parallel run, ~70 ms alone, three of three).
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

        [Fact]
        public async Task A_guarded_client_refuses_a_loopback_receiver_at_connect()
        {
            await using var receiver = new LoopbackWebhookReceiver();
            using var client = new HostConnectorEgress(Array.Empty<string>()).CreateHttpClient(Timeout);

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(receiver.Url()));

            Assert.True(HostConnectorEgress.IsDenial(ex), ex.ToString());
            Assert.Empty(receiver.Requests);
        }

        [Fact]
        public async Task A_name_resolving_to_loopback_is_refused_at_connect()
        {
            await using var receiver = new LoopbackWebhookReceiver();
            using var client = new HostConnectorEgress(Array.Empty<string>()).CreateHttpClient(Timeout);

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync($"http://localhost:{receiver.Port}/hook"));

            Assert.True(HostConnectorEgress.IsDenial(ex), ex.ToString());
            Assert.Empty(receiver.Requests);
        }

        [Fact]
        public async Task An_allowlisted_loopback_receiver_receives_the_request()
        {
            await using var receiver = new LoopbackWebhookReceiver();
            using var client = new HostConnectorEgress(new[] { "127.0.0.1" }).CreateHttpClient(Timeout);

            var response = await client.GetAsync(receiver.Url("/probe"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("/probe", Assert.Single(receiver.Requests).Path);
        }

        [Fact]
        public async Task The_allowlist_admits_only_the_named_host()
        {
            await using var receiver = new LoopbackWebhookReceiver();
            using var client = new HostConnectorEgress(new[] { "localhost" }).CreateHttpClient(Timeout);

            var ex = await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync(receiver.Url()));

            Assert.True(HostConnectorEgress.IsDenial(ex), ex.ToString());
            Assert.Empty(receiver.Requests);
        }

        [Fact]
        public async Task A_redirect_is_not_followed()
        {
            await using var receiver = new LoopbackWebhookReceiver { Status = HttpStatusCode.Found, Location = "http://169.254.169.254/latest/meta-data/" };
            using var client = new HostConnectorEgress(new[] { "127.0.0.1" }).CreateHttpClient(Timeout);

            var response = await client.GetAsync(receiver.Url());

            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
            Assert.Single(receiver.Requests);
        }

        [Fact]
        public async Task Resolve_and_pin_refuses_a_denied_address_and_admits_an_allowlisted_or_public_one()
        {
            var egress = new HostConnectorEgress(Array.Empty<string>());
            var allowlisted = new HostConnectorEgress(new[] { "127.0.0.1", "10.0.0.5" });

            Assert.Null(await egress.ResolveAndPinAsync("127.0.0.1", 5432));
            Assert.Null(await egress.ResolveAndPinAsync("169.254.169.254", 80));
            Assert.Null(await egress.ResolveAndPinAsync("10.0.0.5", 5432));
            Assert.Null(await egress.ResolveAndPinAsync("[::1]", 5432));
            Assert.Equal(IPAddress.Parse("93.184.216.34"), await egress.ResolveAndPinAsync("93.184.216.34", 443));
            Assert.Equal(IPAddress.Loopback, await allowlisted.ResolveAndPinAsync("127.0.0.1", 5432));
            Assert.Equal(IPAddress.Parse("10.0.0.5"), await allowlisted.ResolveAndPinAsync("10.0.0.5", 5432));
            Assert.Null(await egress.ResolveAndPinAsync("", 1));
            Assert.Null(await egress.ResolveAndPinAsync("no-such-host.invalid", 1));
        }

        [Fact]
        public void The_site_allowlist_setting_parses_like_the_webhook_one()
        {
            Assert.Empty(SiteSettingConnectorEgressFactory.ParseAllowedHosts(null));
            Assert.Equal(new[] { "db.internal", "10.0.0.5", "::1" }, SiteSettingConnectorEgressFactory.ParseAllowedHosts(" db.internal, 10.0.0.5; [::1]\n DB.INTERNAL "));
        }
    }
}
