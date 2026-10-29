#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Mediator;
using Stomme.Models.Notifications;
using Stomme.Modules.Notifications.Application.Templates;
using Stomme.Notifications;
using Stomme.Notifications.Persistence;
using Stomme.Security.Abstractions;
using Stomme.SharedKernel.Domain;
using Stomme.Shared;
using FluentValidation;

namespace Stomme.Modules.Notifications.Application
{
    // The two operator commands gated by Send. NOT a lift of the predecessor platform's SendNotificationCommand /
    // RetryDeliveryCommand: those were UNGATED, system-scoped commands the bus consumer ran - in Stomme that work is the
    // dispatch job's (NotificationDispatchProcessor, no [Requires], F3). These are the user-facing surface D100 point 6 names:
    // "send/retry = send".

    /// <summary>
    /// Sends a test notification TO THE CALLER through <c>generic.message</c> over the given channels, through the SAME
    /// <see cref="INotificationDispatcher"/> port every producer uses (so the recipient constraint applies) - the job delivers it.
    /// </summary>
    [Requires(NotificationsPermissions.Send)]
    public sealed record SendTestNotificationCommand(IReadOnlyList<NotificationChannelKind> Channels, string Subject, string Body) : ICommand<Guid>;

    public sealed class SendTestNotificationCommandValidator : AbstractValidator<SendTestNotificationCommand>
    {
        public SendTestNotificationCommandValidator()
        {
            RuleFor(command => command.Channels).NotEmpty();
            RuleFor(command => command.Subject).NotEmpty().MaximumLength(200);
            RuleFor(command => command.Body).NotEmpty().MaximumLength(4000);
        }
    }

    public sealed class SendTestNotificationCommandHandler(INotificationDispatcher dispatcher, ICurrentUser currentUser)
        : ICommandHandler<SendTestNotificationCommand, Guid>
    {
        public Task<Guid> Handle(SendTestNotificationCommand request, CancellationToken ct)
        {
            if (currentUser.UserId is not { } userId || userId <= 0)
            {
                throw new InvalidOperationException("An authenticated user is required to send a test notification.");
            }

            return dispatcher.DispatchAsync(
                new NotificationDispatchRequest(
                    NotificationTemplateCatalogue.GenericMessageKey,
                    new[] { new DispatchRecipient(NotificationRecipientKind.User, userId.ToString(CultureInfo.InvariantCulture)) },
                    request.Channels,
                    new[] { new DispatchParameter("subject", request.Subject), new DispatchParameter("body", request.Body) }),
                ct);
        }
    }

    /// <summary>
    /// Re-opens a FINISHED request of the current site that did not fully send, so the next dispatch pass re-attempts its
    /// unresolved channels with a fresh retry budget. A request still in flight, or fully sent, is refused.
    /// </summary>
    [Requires(NotificationsPermissions.Send)]
    public sealed record RetryNotificationRequestCommand(Guid Id) : ICommand<Unit>;

    public sealed class RetryNotificationRequestCommandHandler(
        INotificationRequestRepository requests, IUnitOfWork unitOfWork, TimeProvider clock, SiteState siteState)
        : ICommandHandler<RetryNotificationRequestCommand, Unit>
    {
        public async Task<Unit> Handle(RetryNotificationRequestCommand request, CancellationToken ct)
        {
            var item = await requests.GetByIdAsync(request.Id, ct);
            if (item is null || item.SiteId != NotificationWire.RequireSiteId(siteState))
            {
                throw new InvalidOperationException($"Notification request '{request.Id}' does not exist.");
            }

            item.Reopen(clock.GetUtcNow().UtcDateTime);
            await requests.UpdateAsync(item, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Unit.Value;
        }
    }
}