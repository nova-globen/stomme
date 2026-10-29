using System;
using Stomme.SharedKernel.Domain;

namespace Stomme.Modules.Guestbook.Domain
{
    /// <summary>
    /// A DOMAIN EVENT (<see cref="DomainEvent"/> / <see cref="IDomainEvent"/>) raised by
    /// <see cref="GuestbookEntryAggregate"/> when a guestbook is signed. It is dispatched IN-PROCESS, in the
    /// caller's unit of work, by the framework's <c>CurrentScopeDomainEventDispatcher</c> when
    /// <c>IUnitOfWork.SaveChangesAsync</c> drains the aggregate — the same mechanism
    /// FeatureManagement's audit trail rides.
    /// <para>
    /// This is DELIBERATELY distinct from <c>GuestbookSignedNotification</c> (the mediator's
    /// <c>INotification</c>). The two dispatch mechanisms COEXIST in this sample on purpose (the sample does NOT
    /// collapse them): the domain event is internal bounded-context
    /// fan-out via the dispatcher, the notification is the mediator's publish path. The module shows both
    /// firing so the difference is visible rather than theoretical.
    /// </para>
    /// </summary>
    public sealed record GuestbookEntrySignedDomainEvent(Guid EntryId, int ModuleId, string Author) : DomainEvent;
}
