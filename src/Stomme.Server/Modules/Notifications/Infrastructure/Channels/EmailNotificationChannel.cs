#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Models;
using Stomme.Modules.Notifications.Domain;
using Stomme.Repository;

namespace Stomme.Modules.Notifications.Infrastructure.Channels
{
    /// <summary>
    /// The e-mail channel: NO SMTP client. It writes a <see cref="Notification"/> row through the EXISTING
    /// <see cref="INotificationRepository.AddNotificationAsync"/> - the inherited queue the per-minute
    /// <c>NotificationJob</c> sends with MailKit under the site's SMTP settings and the <c>notifications.email</c> flag -
    /// so there is one e-mail path, not two. The row is undelivered, due now (<c>SendOn = null</c>), and a SYSTEM
    /// notification (<c>FromUserId = null</c>): the inherited purge removes only <c>FromUserId == null</c> rows (F16), so
    /// a row with a sender would never be purged. The rendered payload arrives HTML-encoded (the renderer's e-mail
    /// target), because the job sends every body as <c>text/html</c>.
    /// </summary>
    public sealed class EmailNotificationChannel : INotificationChannel
    {
        /// <summary>The <c>Notification.Subject</c> bound (256 per the builder; a 256-character subject is driven on every store).</summary>
        public const int MaxSubjectLength = 256;

        /// <summary>The <c>Notification.ToEmail</c> bound (256 per the builder).</summary>
        public const int MaxEmailLength = 256;

        private readonly INotificationRepository _notifications;

        public EmailNotificationChannel(INotificationRepository notifications) =>
            _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));

        public ChannelKind Kind => ChannelKind.Email;

        public string ProviderKey => NotificationProviderKeys.Email;

        /// <summary>Nothing is required: the SMTP host and credentials are the site's existing SMTP settings, not the channel's.</summary>
        public Task<IReadOnlyList<string>> ValidateSettingsAsync(int siteId, TransportSettings settings, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(settings);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public async Task<ChannelDeliveryResult> SendAsync(ChannelDeliveryRequest request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ct.ThrowIfCancellationRequested();

            if (request.Payload.Subject.Length > MaxSubjectLength)
            {
                return ChannelDeliveryResult.Fail($"The rendered e-mail subject is {request.Payload.Subject.Length} characters; the queue holds at most {MaxSubjectLength}.");
            }

            var notification = NotificationRows.SystemRow(request.SiteId, request.Payload);
            switch (request.Recipient.Kind)
            {
                case RecipientKind.User:
                    notification.ToUserId = int.Parse(request.Recipient.Address, NumberStyles.None, CultureInfo.InvariantCulture);
                    break;
                case RecipientKind.Email:
                    if (request.Recipient.Address.Length > MaxEmailLength)
                    {
                        return ChannelDeliveryResult.Fail($"The e-mail address is longer than {MaxEmailLength} characters.");
                    }

                    notification.ToEmail = request.Recipient.Address;
                    break;
                default:
                    return ChannelDeliveryResult.Fail($"An e-mail cannot be addressed to a {request.Recipient.Kind} recipient.");
            }

            try
            {
                await _notifications.AddNotificationAsync(notification);
                return ChannelDeliveryResult.Ok();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The exception TYPE only: a store message can carry a connection string.
                return ChannelDeliveryResult.Fail($"The e-mail could not be queued ({ex.GetType().Name}).");
            }
        }
    }

    /// <summary>The inherited <see cref="Notification"/> row shape the e-mail and in-app channels write.</summary>
    internal static class NotificationRows
    {
        public static Notification SystemRow(int siteId, Payload payload) => new()
        {
            SiteId = siteId,
            FromUserId = null,
            FromDisplayName = string.Empty,
            FromEmail = string.Empty,
            ToUserId = null,
            ToDisplayName = string.Empty,
            ToEmail = string.Empty,
            ParentId = null,
            Subject = payload.Subject,
            Body = payload.Body,
            CreatedOn = DateTime.UtcNow,
            IsDelivered = false,
            DeliveredOn = null,
            SendOn = null,
            IsRead = false,
            IsDeleted = false,
        };
    }
}
