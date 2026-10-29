#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Infrastructure.Security;
using Stomme.Models;
using Stomme.Modules.Notifications.Domain;
using Stomme.Repository;
using Stomme.Shared;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Notifications.Infrastructure.Channels
{
    /// <summary>
    /// The webhook channel (D100 point 2): one HTTP POST of a small JSON envelope (<c>to</c>/<c>subject</c>/<c>body</c>) to
    /// the channel's endpoint, lifted from the predecessor platform <c>WebhookChannelProvider</c> and closed against the SSRF path it had.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>SSRF guard - the framework's ONE outbound guard (<see cref="OutboundConnectGuard"/>, BL-132).</b> Checked at CONFIGURE time (<see cref="ValidateSettingsAsync"/>
    /// resolves the host) AND at SEND time inside the guarded client's connect callback, on the addresses actually
    /// connected to - so a name that resolved to a public address at save and to loopback at send (DNS rebinding) is
    /// still refused. Redirects are never followed, the client uses no proxy, http/https only. A site may allowlist
    /// hosts through the <c>NotificationWebhookAllowedHosts</c> site setting (an in-test loopback receiver, an intranet
    /// integration the operator chose). Every request carries <c>Connection: close</c>, so a pooled connection opened
    /// under one site's allowlist is never reused by another site's request.
    /// </para>
    /// <para>
    /// <b>Credential.</b> Optional (Slack/Teams/Discord authenticate by URL). When the transport carries a secret
    /// reference - the D101 key <see cref="CredentialReferenceKey"/> - it is read from <see cref="ISiteSecretStore"/> at
    /// send time and sent as a Bearer token through the guarded client only. A missing or unreadable secret (F8: a lost
    /// Data Protection key ring) is a FAILED attempt with a message that names neither the secret nor the ciphertext.
    /// </para>
    /// <para>
    /// <b>Body.</b> The payload arrives RAW (the renderer's webhook target) and is JSON-escaped exactly once, here, by the
    /// serializer's default encoder (which also escapes <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> and quotes).
    /// </para>
    /// </remarks>
    public sealed class WebhookNotificationChannel : INotificationChannel
    {
        private readonly WebhookHttpClient _http;
        private readonly ISiteSecretStore _secrets;
        private readonly IWebhookHostAllowlist _allowlist;
        private readonly ILogger<WebhookNotificationChannel> _logger;

        public WebhookNotificationChannel(
            WebhookHttpClient http, ISiteSecretStore secrets, IWebhookHostAllowlist allowlist, ILogger<WebhookNotificationChannel> logger)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
            _allowlist = allowlist ?? throw new ArgumentNullException(nameof(allowlist));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public ChannelKind Kind => ChannelKind.Webhook;

        public string ProviderKey => NotificationProviderKeys.Webhook;

        /// <summary>The D101 site secret store reference key for a channel's credential: <c>notifications.channel.{id:N}.credential</c>.</summary>
        public static string CredentialReferenceKey(Guid channelId) => $"notifications.channel.{channelId:N}.credential";

        /// <summary>The JSON envelope posted to the endpoint; the only place the rendered text is escaped.</summary>
        public static string BuildEnvelope(Recipient recipient, Payload payload)
        {
            ArgumentNullException.ThrowIfNull(recipient);
            ArgumentNullException.ThrowIfNull(payload);
            return JsonSerializer.Serialize(new WebhookEnvelope(recipient.Value, payload.Subject, payload.Body));
        }

        public async Task<IReadOnlyList<string>> ValidateSettingsAsync(int siteId, TransportSettings settings, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(settings);
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(settings.Endpoint, out var endpoint))
            {
                return ["A webhook channel requires an absolute http(s) endpoint URL."];
            }

            var allowed = await _allowlist.GetAllowedHostsAsync(siteId, ct);
            var denial = await WebhookSsrfGuard.CheckDestinationAsync(endpoint, allowed, ct);
            return denial is null ? [] : [denial];
        }

        public async Task<ChannelDeliveryResult> SendAsync(ChannelDeliveryRequest request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            var transport = request.Channel.Transport;
            if (!Stomme.Shared.Utilities.TryCreateAbsoluteUri(transport.Endpoint, out var endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
            {
                return ChannelDeliveryResult.Fail("The webhook channel has no absolute http(s) endpoint URL.");
            }

            string? token = null;
            if (transport.SecretReference is { } reference)
            {
                var secret = await _secrets.GetAsync(request.SiteId, reference, ct);
                switch (secret.Status)
                {
                    case SiteSecretStatus.Found:
                        token = secret.Secret;
                        break;
                    case SiteSecretStatus.NotFound:
                        return ChannelDeliveryResult.Fail("The webhook credential is not set for this site; set it again.");
                    default:
                        return ChannelDeliveryResult.Fail("The webhook credential cannot be read on this host (it was protected under a key ring this host does not have); set it again.");
                }
            }

            var allowed = await _allowlist.GetAllowedHostsAsync(request.SiteId, ct);
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(BuildEnvelope(request.Recipient, request.Payload), Encoding.UTF8, "application/json"),
            };
            message.Headers.ConnectionClose = true;
            message.Options.Set(OutboundConnectGuard.AllowedHostsOption, allowed);
            if (!string.IsNullOrEmpty(token))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            try
            {
                using var response = await _http.Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
                if (response.IsSuccessStatusCode)
                {
                    return ChannelDeliveryResult.Ok();
                }

                var status = (int)response.StatusCode;
                return ChannelDeliveryResult.Fail(status is >= 300 and < 400
                    ? $"The webhook endpoint returned HTTP {status}; redirects are not followed."
                    : $"The webhook endpoint returned HTTP {status}.");
            }
            catch (HttpRequestException ex) when (OutboundConnectGuard.FindDenial(ex) is { } denied)
            {
                var refusal = WebhookSsrfGuard.Explain(denied.Message);
                _logger.LogWarning("Webhook channel {ChannelId} on site {SiteId}: {Denial}", request.Channel.Id, request.SiteId, refusal);
                return ChannelDeliveryResult.Fail(refusal);
            }
            catch (HttpRequestException ex)
            {
                return ChannelDeliveryResult.Fail($"The webhook request failed ({ex.HttpRequestError}).");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return ChannelDeliveryResult.Fail("The webhook request timed out.");
            }
        }

        private sealed record WebhookEnvelope(
            [property: System.Text.Json.Serialization.JsonPropertyName("to")] string To,
            [property: System.Text.Json.Serialization.JsonPropertyName("subject")] string Subject,
            [property: System.Text.Json.Serialization.JsonPropertyName("body")] string Body);
    }

    /// <summary>A site's webhook host allowlist - the hosts the SSRF guard admits even when they resolve to a denied range.</summary>
    public interface IWebhookHostAllowlist
    {
        Task<IReadOnlyCollection<string>> GetAllowedHostsAsync(int siteId, CancellationToken ct = default);
    }

    /// <summary>
    /// The <see cref="IWebhookHostAllowlist"/> read from the site setting <see cref="WebhookSsrfGuard.AllowedHostsSettingName"/>:
    /// host names or IP literals separated by commas, semicolons or whitespace, matched case-insensitively against the
    /// request host exactly (no wildcards, no subdomains). Absent = nothing allowlisted.
    /// </summary>
    public sealed class SiteSettingWebhookHostAllowlist : IWebhookHostAllowlist
    {
        private readonly ISettingRepository _settings;

        public SiteSettingWebhookHostAllowlist(ISettingRepository settings) =>
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        public async Task<IReadOnlyCollection<string>> GetAllowedHostsAsync(int siteId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var setting = await _settings.GetSettingAsync(EntityNames.Site, siteId, WebhookSsrfGuard.AllowedHostsSettingName);
            return Parse(setting?.SettingValue);
        }

        public static IReadOnlyCollection<string> Parse(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? []
                : value.Split([',', ';', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(host => host.Trim().Trim('[', ']'))
                    .Where(host => host.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
    }
}
