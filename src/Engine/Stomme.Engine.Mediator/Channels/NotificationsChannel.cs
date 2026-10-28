using Stomme.Engine.Mediator.Abstractions;
using Stomme.Engine.Mediator.Contracts;
using Stomme.Engine.Mediator.Middleware.Notification;

namespace Stomme.Engine.Mediator.Channels;

/// <inheritdoc cref="Stomme.Engine.Mediator.Contracts.INotificationsChannel" />
public class NotificationsChannel : ChannelBase<NotificationContext>, INotificationsChannel
{
}