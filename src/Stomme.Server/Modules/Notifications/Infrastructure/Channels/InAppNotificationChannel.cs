#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Notifications.Domain;
using Stomme.Repository;
using Stomme.Transport.Realtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Notifications.Infrastructure.Channels
{
    /// <summary>
    /// The in-app channel: writes the inherited <c>Notification</c> row ALREADY DELIVERED
    /// (<c>IsDelivered = true</c>, <c>DeliveredOn = UtcNow</c>) - so the SMTP queue predicate never returns it - and then
    /// pushes it live through <see cref="INotificationRealtimePush"/> when the host has one. The push port is resolved
    /// OPTIONALLY: it is registered only when the SignalR transport is enabled, so its absence is not a failure (the
    /// row IS the delivery - failing the send instead would be wrong). A push that throws is logged and does not undo a written
    /// row. Only a site user can be addressed.
    /// </summary>
    public sealed class InAppNotificationChannel : INotificationChannel
    {
        private readonly INotificationRepository _notifications;
        private readonly IServiceProvider _services;
        private readonly ILogger<InAppNotificationChannel> _logger;

        public InAppNotificationChannel(INotificationRepository notifications, IServiceProvider services, ILogger<InAppNotificationChannel> logger)
        {
            _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public ChannelKind Kind => ChannelKind.InApp;

        public string ProviderKey => NotificationProviderKeys.InApp;

        public Task<IReadOnlyList<string>> ValidateSettingsAsync(int siteId, TransportSettings settings, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(settings);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public async Task<ChannelDeliveryResult> SendAsync(ChannelDeliveryRequest request, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ct.ThrowIfCancellationRequested();

            if (request.Recipient.Kind != RecipientKind.User)
            {
                return ChannelDeliveryResult.Fail("In-app delivery targets a site user; the recipient is not a user.");
            }

            if (request.Payload.Subject.Length > EmailNotificationChannel.MaxSubjectLength)
            {
                return ChannelDeliveryResult.Fail($"The rendered subject is {request.Payload.Subject.Length} characters; the inbox holds at most {EmailNotificationChannel.MaxSubjectLength}.");
            }

            var userId = int.Parse(request.Recipient.Address, NumberStyles.None, CultureInfo.InvariantCulture);
            var notification = NotificationRows.SystemRow(request.SiteId, request.Payload);
            notification.ToUserId = userId;
            notification.IsDelivered = true;
            notification.DeliveredOn = DateTime.UtcNow;

            try
            {
                await _notifications.AddNotificationAsync(notification);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ChannelDeliveryResult.Fail($"The in-app notification could not be stored ({ex.GetType().Name}).");
            }

            var push = _services.GetService<INotificationRealtimePush>();
            if (push is not null)
            {
                try
                {
                    await push.PushAsync(
                        new NotificationRealtimeMessage(userId.ToString(CultureInfo.InvariantCulture), request.Payload.Subject, request.Payload.Body), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("The live push of in-app notification {NotificationId} failed ({ExceptionType}); the inbox row stands.",
                        notification.NotificationId, ex.GetType().Name);
                }
            }

            return ChannelDeliveryResult.Ok();
        }
    }
}
