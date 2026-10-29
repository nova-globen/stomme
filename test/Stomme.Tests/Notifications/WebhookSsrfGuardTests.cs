using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Modules.Notifications.Domain;
using Stomme.Modules.Notifications.Infrastructure.Channels;
using Stomme.Tenancy.Domain;
using Xunit;

namespace Stomme.Tests.Notifications
{
    /// <summary>
    /// The webhook SSRF guard. Every denied range is refused at CONFIGURE time
    /// (<c>ValidateSettingsAsync</c>) and at SEND time; the send-time check runs inside
    /// <c>SocketsHttpHandler.ConnectCallback</c> on the addresses actually connected to (a DNS name that resolves to
    /// loopback is refused AT CONNECT, which is what defeats rebinding); redirects are never followed; a site's
    /// allowlisted in-test loopback receiver still RECEIVES the body.
    /// <para>Validated both directions: with <c>IsDenied</c> stubbed to allow-all, the denial facts go red (recorded in the step report).</para>
    /// <para><b>Since BL-132 every fact here drives the framework's ONE outbound guard</b> (<see cref="OutboundConnectGuard"/>):
    /// the webhook keeps only its setting name and its wording, and the client's connect step is pinned to the guard's.</para>
    /// </summary>
    public sealed class WebhookSsrfGuardTests
    {
        private const int Site = NotificationTestData.SiteId;

        public static TheoryData<string> DeniedAddresses() => new()
        {
            "127.0.0.1", "127.255.1.2", "::1",                               // loopback
            "10.0.0.1", "10.255.255.255",                                    // RFC1918 10/8
            "172.16.0.1", "172.31.255.254",                                  // RFC1918 172.16/12
            "192.168.10.20", "192.168.255.1",                                 // RFC1918 192.168/16
            "169.254.169.254", "169.254.0.1", "fe80::1",                     // link-local + the metadata endpoint
            "fc00::1", "fdff:ffff::1",                                       // unique local fc00::/7
            "0.0.0.0", "::",                                                 // unspecified
            "::ffff:127.0.0.1", "::ffff:10.1.2.3", "::ffff:172.20.0.1",      // IPv4-mapped IPv6 forms
            "::ffff:192.168.1.1", "::ffff:169.254.169.254", "::ffff:0.0.0.0",
        };

        public static TheoryData<string> PermittedAddresses() => new()
        {
            "8.8.8.8", "1.1.1.1", "172.15.255.255", "172.32.0.1", "192.167.255.255", "169.253.0.1",
            "11.0.0.1", "2001:4860:4860::8888", "::ffff:8.8.8.8",
        };

        [Theory]
        [MemberData(nameof(DeniedAddresses))]
        public void Denied_ranges_are_denied(string address) => Assert.True(OutboundConnectGuard.IsDenied(IPAddress.Parse(address)), address);

        [Theory]
        [MemberData(nameof(PermittedAddresses))]
        public void Public_addresses_are_not_denied(string address) => Assert.False(OutboundConnectGuard.IsDenied(IPAddress.Parse(address)), address);

        private static WebhookNotificationChannel Webhook(params string[] allowedHosts) =>
            new(new WebhookHttpClient(), new FakeSiteSecretStore(), new FixedWebhookHostAllowlist(allowedHosts), new CapturingLogger<WebhookNotificationChannel>());

        private static string UrlFor(string address, int port = 9) =>
            IPAddress.Parse(address).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"http://[{address}]:{port}/hook" : $"http://{address}:{port}/hook";

        private static ChannelDeliveryRequest RequestTo(string url) =>
            new(Site, NotificationTestData.WebhookChannel(url: url), NotificationTestData.Email(), new Payload("S", "B"));

        [Theory]
        [MemberData(nameof(DeniedAddresses))]
        public async Task A_denied_address_is_refused_at_configure_time(string address)
        {
            var errors = await Webhook().ValidateSettingsAsync(Site, NotificationTestData.WebhookTransport(UrlFor(address)));
            Assert.Contains(errors, error => error.Contains("not permitted", StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [MemberData(nameof(DeniedAddresses))]
        public async Task A_denied_address_is_refused_at_send_time(string address)
        {
            var result = await Webhook().SendAsync(RequestTo(UrlFor(address)));
            Assert.False(result.Succeeded);
            Assert.False(result.Skipped);
            Assert.Contains("not permitted", result.Error, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task A_loopback_receiver_that_is_not_allowlisted_receives_nothing()
        {
            await using var receiver = new LoopbackWebhookReceiver();
            var webhook = Webhook();

            Assert.NotEmpty(await webhook.ValidateSettingsAsync(Site, NotificationTestData.WebhookTransport(receiver.Url())));
            var result = await webhook.SendAsync(RequestTo(receiver.Url()));

            Assert.False(result.Succeeded);
            Assert.Contains("not permitted", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(receiver.Requests);
        }

        [Fact]
        public async Task A_dns_name_resolving_to_loopback_is_refused_at_connect()
        {
            await using var receiver = new LoopbackWebhookReceiver();
            var url = $"http://localhost:{receiver.Port}/hook";

            // Configure-time resolution refuses it ...
            Assert.Contains(await Webhook().ValidateSettingsAsync(Site, NotificationTestData.WebhookTransport(url)),
                error => error.Contains("not permitted", StringComparison.OrdinalIgnoreCase));

            // ... and a channel that never went through configure (the rebinding shape: public at save, loopback at send)
            // is refused inside the connect callback, before a byte reaches the receiver.
            var result = await Webhook().SendAsync(RequestTo(url));
            Assert.False(result.Succeeded);
            Assert.Contains("not permitted", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(receiver.Requests);
        }

        [Fact]
        public async Task An_allowlisted_loopback_receiver_receives_the_body()
        {
            await using var receiver = new LoopbackWebhookReceiver();
            var webhook = Webhook("127.0.0.1");

            Assert.Empty(await webhook.ValidateSettingsAsync(Site, NotificationTestData.WebhookTransport(receiver.Url())));
            var result = await webhook.SendAsync(new ChannelDeliveryRequest(Site, NotificationTestData.WebhookChannel(url: receiver.Url()),
                NotificationTestData.Email(), new Payload("Subject", "The body")));

            Assert.True(result.Succeeded, result.Error);
            Assert.Contains("The body", Assert.Single(receiver.Requests).Body);
        }

        [Fact]
        public async Task The_allowlist_admits_only_the_named_host_not_every_denied_address()
        {
            await using var receiver = new LoopbackWebhookReceiver();
            // "localhost" is allowlisted, but the channel addresses 127.0.0.1 literally: a different host, still denied.
            var result = await Webhook("localhost").SendAsync(RequestTo(receiver.Url()));
            Assert.False(result.Succeeded);
            Assert.Empty(receiver.Requests);
        }

        [Fact]
        public async Task A_redirect_is_not_followed()
        {
            await using var target = new LoopbackWebhookReceiver();
            await using var redirector = new LoopbackWebhookReceiver { Status = HttpStatusCode.Redirect };
            redirector.Location = target.Url("/stolen");

            var result = await Webhook("127.0.0.1").SendAsync(RequestTo(redirector.Url()));

            Assert.False(result.Succeeded);
            Assert.Contains("302", result.Error);
            Assert.Single(redirector.Requests);
            Assert.Empty(target.Requests);
        }

        [Theory]
        [InlineData("ftp://hooks.example.com/notify")]
        [InlineData("file:///etc/passwd")]
        public async Task Only_http_and_https_schemes_are_accepted(string url)
        {
            // The domain refuses a non-http(s) webhook endpoint outright; the guard refuses one too, independently.
            Assert.Throws<ArgumentException>(() => NotificationTestData.WebhookTransport(url));
            Assert.NotNull(await WebhookSsrfGuard.CheckDestinationAsync(new Uri(url), []));
        }

        [Fact]
        public void The_guarded_client_follows_no_redirect_and_uses_no_proxy_and_checks_at_connect()
        {
            using var client = new WebhookHttpClient();
            var handler = client.Handler;
            Assert.False(handler.AllowAutoRedirect);
            Assert.False(handler.UseProxy); // a proxy would move the connect-time check onto the proxy, not the destination
            Assert.NotNull(handler.ConnectCallback);
        }

        [Fact]
        public void The_webhook_connects_through_the_frameworks_ONE_guard_not_a_copy()
        {
            using var client = new WebhookHttpClient();
            var callback = client.Handler.ConnectCallback!;
            Assert.Equal(typeof(OutboundConnectGuard), callback.Method.DeclaringType);
            Assert.Equal(nameof(OutboundConnectGuard.ConnectAsync), callback.Method.Name);
        }

        [Fact]
        public async Task A_refusal_tells_the_operator_WHERE_the_webhook_allowlist_is()
        {
            await using var receiver = new LoopbackWebhookReceiver();

            var configure = Assert.Single(await Webhook().ValidateSettingsAsync(Site, NotificationTestData.WebhookTransport(receiver.Url())));
            var send = (await Webhook().SendAsync(RequestTo(receiver.Url()))).Error;

            Assert.All(new[] { configure, send }, message => Assert.Contains(WebhookSsrfGuard.AllowedHostsSettingName, message, StringComparison.Ordinal));
        }
    }
}
