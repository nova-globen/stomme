using System;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Guestbook.Application
{
    /// <summary>
    /// The MEDIATOR notification published by <c>SignGuestbookCommandHandler</c> after a signature is written,
    /// via <c>IMediator.Publish</c>. This is the mediator's <see cref="INotification"/> fan-out path — one of
    /// the two in-process mechanisms this sample runs side by side (the other is the framework domain-event
    /// dispatcher; see <c>GuestbookEntrySignedDomainEvent</c>). They coexist on purpose.
    /// </summary>
    public sealed record GuestbookSignedNotification(Guid EntryId, int ModuleId, string Author) : INotification;

    /// <summary>
    /// An in-process handler for the notification. A real module would send an e-mail, bump a counter or
    /// invalidate a cache here; this one logs, which is enough to make the mediator's publish path observable
    /// when the command is driven.
    /// </summary>
    public sealed class GuestbookSignedNotificationHandler(ILogger<GuestbookSignedNotificationHandler> logger)
        : INotificationHandler<GuestbookSignedNotification>
    {
        public Task Handle(GuestbookSignedNotification notification, CancellationToken ct)
        {
            logger.LogInformation(
                "[Guestbook] MEDIATOR INotification: {Author} signed guestbook module {ModuleId} (entry {EntryId}).",
                notification.Author, notification.ModuleId, notification.EntryId);
            return Task.CompletedTask;
        }
    }
}
