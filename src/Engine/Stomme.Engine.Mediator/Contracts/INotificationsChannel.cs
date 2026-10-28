using System.Threading.Channels;
using Stomme.Engine.Mediator.Middleware.Notification;

namespace Stomme.Engine.Mediator.Contracts;

/// <summary>
/// A channel that can be used to enqueue notifications.
/// </summary>
public interface INotificationsChannel
{
    /// <summary>
    /// Gets the writer for the notification queue.
    /// </summary>
    ChannelWriter<NotificationContext> Writer { get; }
    
    /// <summary>
    /// Gets the reader for the notification queue.
    /// </summary>
    ChannelReader<NotificationContext> Reader { get; }
}