#nullable enable
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;

namespace Stomme.Modules.Notifications.Infrastructure.Channels
{
    /// <summary>
    /// The webhook channel's use of the framework's ONE outbound guard (collapsed). A
    /// webhook endpoint is operator-typed and a notification is sent by the server, so without a guard a channel is a way
    /// to make the host POST to its own loopback services, the LAN (the shared infra tier is RFC1918) or the cloud metadata
    /// endpoint.
    /// </summary>
    /// <remarks>
    /// The deny set, the configure-time check and the connect-time check are <see cref="OutboundConnectGuard"/>'s - this
    /// class once carried a line-for-line copy of all three. What is left is what is genuinely the webhook's: the SITE
    /// SETTING that holds its allowlist, and wording that tells an operator where that setting is.
    /// </remarks>
    public static class WebhookSsrfGuard
    {
        /// <summary>The site setting holding the host allowlist (see <see cref="SiteSettingWebhookHostAllowlist"/>).</summary>
        public const string AllowedHostsSettingName = "NotificationWebhookAllowedHosts";

        /// <summary>
        /// The CONFIGURE-time check (<see cref="OutboundConnectGuard.CheckDestinationAsync"/>), worded for the webhook.
        /// Returns the refusal, or null when the destination is permitted.
        /// </summary>
        public static async Task<string?> CheckDestinationAsync(Uri endpoint, IReadOnlyCollection<string> allowedHosts, CancellationToken ct = default)
        {
            var denial = await OutboundConnectGuard.CheckDestinationAsync(endpoint, allowedHosts, ct);
            return denial is null ? null : Explain(denial);
        }

        /// <summary>A refusal from the guard, with where an operator can allowlist the host.</summary>
        public static string Explain(string denial) =>
            $"Webhook: {denial} An operator can allowlist the host in the site setting '{AllowedHostsSettingName}'.";
    }

    /// <summary>
    /// The ONE guarded <see cref="HttpClient"/> the webhook channel sends through (a singleton - its handler pools
    /// sockets), over <see cref="OutboundConnectGuard.CreateHandler"/>. A dedicated type rather than a bare
    /// <c>HttpClient</c> registration, so no other component's client can be substituted for it by container order.
    /// </summary>
    public sealed class WebhookHttpClient : IDisposable
    {
        public WebhookHttpClient()
        {
            Handler = OutboundConnectGuard.CreateHandler();
            Client = new HttpClient(Handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        }

        public SocketsHttpHandler Handler { get; }

        public HttpClient Client { get; }

        public void Dispose() => Client.Dispose();
    }
}
