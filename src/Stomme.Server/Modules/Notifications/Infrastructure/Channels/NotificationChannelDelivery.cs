#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Notifications.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Notifications.Infrastructure.Channels
{
    /// <summary>
    /// One channel send, recorded: resolve the transport for a configured channel, send, and record the outcome on the
    /// request as a <see cref="DeliveryAttempt"/> - <see cref="DeliveryStatus.Skipped"/> when the resolver skips (with its
    /// reason) or the transport reports a skip, <see cref="DeliveryStatus.Succeeded"/> or
    /// <see cref="DeliveryStatus.Failed"/> otherwise. A transport that THROWS is a failed attempt naming only the
    /// exception type (a message can carry a credential or a connection string). The retry/deferral policy that decides
    /// WHEN to call this belongs to <c>NotificationDispatchProcessor</c> (the dispatch job's pass).
    /// </summary>
    public sealed class NotificationChannelDelivery
    {
        private readonly NotificationChannelResolver _resolver;
        private readonly ILogger<NotificationChannelDelivery> _logger;

        public NotificationChannelDelivery(NotificationChannelResolver resolver, ILogger<NotificationChannelDelivery> logger)
        {
            _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>One recipient over one configured channel: one attempt.</summary>
        public Task<DeliveryAttempt> DeliverAsync(
            int siteId, NotificationRequestAggregate request, NotificationChannelAggregate channel, Recipient recipient, Payload payload,
            DateTime attemptedOnUtc, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(recipient);
            return DeliverToRecipientsAsync(siteId, request, channel, [recipient], payload, attemptedOnUtc, ct);
        }

        /// <summary>
        /// Every eligible recipient over one configured channel, recorded as ONE attempt for the channel (BL-78 step 4 - the
        /// dispatch pass's fan-out, as the predecessor platform's <c>DeliverOverChannelAsync</c>): resolved once; <see cref="DeliveryStatus.Failed"/>
        /// when any send failed (the reasons joined), <see cref="DeliveryStatus.Skipped"/> when every send was a deliberate
        /// skip, <see cref="DeliveryStatus.Succeeded"/> otherwise.
        /// </summary>
        public async Task<DeliveryAttempt> DeliverToRecipientsAsync(
            int siteId, NotificationRequestAggregate request, NotificationChannelAggregate channel, IReadOnlyList<Recipient> recipients, Payload payload,
            DateTime attemptedOnUtc, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(channel);
            ArgumentNullException.ThrowIfNull(recipients);
            ArgumentNullException.ThrowIfNull(payload);

            if (request.SiteId != siteId || channel.SiteId != siteId)
            {
                // A cross-site pairing is a caller defect; recorded, never sent (a site's channel must not carry another site's notification).
                return request.RecordAttempt(channel.Kind, DeliveryStatus.Failed, attemptedOnUtc,
                    $"The channel (site {channel.SiteId}) and the request (site {request.SiteId}) do not both belong to site {siteId}.");
            }

            var resolution = _resolver.Resolve(channel);
            if (resolution.Channel is null)
            {
                return request.RecordAttempt(channel.Kind, DeliveryStatus.Skipped, attemptedOnUtc, resolution.SkipReason);
            }

            var errors = new List<string>();
            string? skipReason = null;
            var skipped = 0;
            foreach (var recipient in recipients)
            {
                ArgumentNullException.ThrowIfNull(recipient);
                ChannelDeliveryResult result;
                try
                {
                    result = await resolution.Channel.SendAsync(new ChannelDeliveryRequest(siteId, channel, recipient, payload), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("The {Kind} transport '{Provider}' threw {ExceptionType} delivering request {RequestId} over channel {ChannelId}.",
                        channel.Kind, resolution.Channel.ProviderKey, ex.GetType().Name, request.Id, channel.Id);
                    errors.Add($"The {channel.Kind} transport failed unexpectedly ({ex.GetType().Name}).");
                    continue;
                }

                if (result.Skipped)
                {
                    skipped++;
                    skipReason ??= result.Error;
                }
                else if (!result.Succeeded)
                {
                    errors.Add(string.IsNullOrWhiteSpace(result.Error) ? $"The {channel.Kind} transport reported a failure." : result.Error);
                }
            }

            if (errors.Count > 0)
            {
                return request.RecordAttempt(channel.Kind, DeliveryStatus.Failed, attemptedOnUtc, string.Join("; ", errors));
            }

            return skipped > 0 && skipped == recipients.Count
                ? request.RecordAttempt(channel.Kind, DeliveryStatus.Skipped, attemptedOnUtc, skipReason)
                : request.RecordAttempt(channel.Kind, DeliveryStatus.Succeeded, attemptedOnUtc);
        }
    }
}
