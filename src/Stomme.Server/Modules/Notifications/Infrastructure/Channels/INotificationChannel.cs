#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Notifications.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Notifications.Infrastructure.Channels
{
    /// <summary>
    /// One delivery medium's transport.
    /// Each implementation owns the kind-specific RUNTIME validation of a channel's <see cref="TransportSettings"/>
    /// (beyond the structural checks the value object enforces) and the actual send. Registered as
    /// <c>IEnumerable&lt;INotificationChannel&gt;</c> by the module startup and selected by
    /// <see cref="NotificationChannelResolver"/>.
    /// </summary>
    /// <remarks>
    /// <b>Why validation is async and takes the SITE.</b> A synchronous <c>IReadOnlyList&lt;string&gt;
    /// ValidateSettings(TransportSettings)</c> is replaced by <see cref="ValidateSettingsAsync"/>: the webhook
    /// channel's configure-time SSRF check must RESOLVE the host (DNS is I/O - a synchronous resolve would block a
    /// request thread, an async one behind a sync signature would be sync-over-async, which this codebase bans) and must honour
    /// the per-site host allowlist, which is keyed by site.
    /// </remarks>
    public interface INotificationChannel
    {
        /// <summary>The channel kind this transport serves.</summary>
        ChannelKind Kind { get; }

        /// <summary>
        /// A stable discriminator within <see cref="Kind"/>, matched against a channel's <c>provider</c> transport option
        /// (<see cref="NotificationProviderKeys.SelectorOption"/>) when a kind has more than one transport.
        /// </summary>
        string ProviderKey { get; }

        /// <summary>Provider-level validation of the settings for a channel of <paramref name="siteId"/>; empty when valid.</summary>
        Task<IReadOnlyList<string>> ValidateSettingsAsync(int siteId, TransportSettings settings, CancellationToken ct = default);

        /// <summary>Delivers one rendered payload to one recipient. Reports a failure as a result, never by throwing.</summary>
        Task<ChannelDeliveryResult> SendAsync(ChannelDeliveryRequest request, CancellationToken ct = default);
    }

    /// <summary>Stable provider discriminators within a <see cref="ChannelKind"/>.</summary>
    public static class NotificationProviderKeys
    {
        /// <summary>The e-mail channel over the inherited notification queue (<c>NotificationJob</c> sends it).</summary>
        public const string Email = "email";

        public const string InApp = "in-app";

        public const string Webhook = "webhook";

        /// <summary>The default key for SMS. NO provider ships, so an SMS channel always resolves to a skip.</summary>
        public const string Sms = "sms";

        /// <summary>The transport-option key a channel uses to select a named provider within its kind.</summary>
        public const string SelectorOption = "provider";

        /// <summary>The default provider key for a kind when a channel does not name one.</summary>
        public static string DefaultFor(ChannelKind kind) => kind switch
        {
            ChannelKind.Email => Email,
            ChannelKind.InApp => InApp,
            ChannelKind.Webhook => Webhook,
            ChannelKind.Sms => Sms,
            _ => string.Empty,
        };
    }

    /// <summary>
    /// One send over a resolved channel. <see cref="SiteId"/> is carried because the
    /// <c>Notification</c> row the e-mail and in-app channels write requires one, and the webhook credential and host
    /// allowlist are per site.
    /// </summary>
    public sealed record ChannelDeliveryRequest(int SiteId, NotificationChannelAggregate Channel, Recipient Recipient, Payload Payload);

    /// <summary>The outcome of a single channel send: success, a failure with a reason, or a deliberate skip.</summary>
    public sealed record ChannelDeliveryResult(bool Succeeded, string? Error, bool Skipped)
    {
        public static ChannelDeliveryResult Ok() => new(true, null, false);

        public static ChannelDeliveryResult Fail(string error) => new(false, error, false);

        public static ChannelDeliveryResult Skip(string reason) => new(false, reason, true);
    }

    /// <summary>A resolution: the transport to use, or null with the reason it is skipped.</summary>
    public sealed record NotificationChannelResolution(INotificationChannel? Channel, string? SkipReason);

    /// <summary>
    /// Selects the <see cref="INotificationChannel"/> for a configured channel. Null - a SKIP, logged with its reason -
    /// for a disabled channel, a kind with no registered transport (SMS), and a channel that NAMES a provider no
    /// transport registers. Falling back to the first same-kind provider in that last case (<c>?? candidates[0]</c>)
    /// would silently deliver over a transport the operator did not choose, so there is no such fallback.
    /// </summary>
    public sealed class NotificationChannelResolver
    {
        private readonly IReadOnlyList<INotificationChannel> _channels;
        private readonly ILogger<NotificationChannelResolver> _logger;

        public NotificationChannelResolver(IEnumerable<INotificationChannel> channels, ILogger<NotificationChannelResolver> logger)
        {
            _channels = (channels ?? throw new ArgumentNullException(nameof(channels))).ToList();
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>The transport for a kind and provider key (the kind's default when none is given); null when none matches.</summary>
        public INotificationChannel? For(ChannelKind kind, string? providerKey = null)
        {
            var wanted = string.IsNullOrWhiteSpace(providerKey) ? NotificationProviderKeys.DefaultFor(kind) : providerKey.Trim();
            return _channels.FirstOrDefault(channel =>
                channel.Kind == kind && string.Equals(channel.ProviderKey, wanted, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>The transport that should deliver for <paramref name="channel"/>, or null (with the reason) when it is skipped.</summary>
        public NotificationChannelResolution Resolve(NotificationChannelAggregate channel)
        {
            ArgumentNullException.ThrowIfNull(channel);

            if (!channel.Enabled)
            {
                return Skipped(channel, $"The {channel.Kind} channel '{channel.Name}' is disabled.");
            }

            if (_channels.All(transport => transport.Kind != channel.Kind))
            {
                return Skipped(channel, $"No transport is registered for {channel.Kind} channels ('{channel.Name}').");
            }

            var named = channel.Transport.Options
                .FirstOrDefault(option => string.Equals(option.Key, NotificationProviderKeys.SelectorOption, StringComparison.OrdinalIgnoreCase))
                ?.Value;
            var resolved = For(channel.Kind, named);
            if (resolved is null)
            {
                return Skipped(channel, string.IsNullOrWhiteSpace(named)
                    ? $"The {channel.Kind} channel '{channel.Name}' names no provider and no transport has the default key '{NotificationProviderKeys.DefaultFor(channel.Kind)}'; none is substituted."
                    : $"The {channel.Kind} channel '{channel.Name}' names provider '{named.Trim()}', which no transport registers; it is not substituted.");
            }

            return new NotificationChannelResolution(resolved, null);
        }

        private NotificationChannelResolution Skipped(NotificationChannelAggregate channel, string reason)
        {
            _logger.LogInformation("Notification channel {ChannelId} on site {SiteId} is skipped: {Reason}", channel.Id, channel.SiteId, reason);
            return new NotificationChannelResolution(null, reason);
        }

        /// <summary>The transport for <paramref name="channel"/>, or null when it is skipped (the reason is logged).</summary>
        public INotificationChannel? ResolveForDelivery(NotificationChannelAggregate channel) => Resolve(channel).Channel;
    }
}
