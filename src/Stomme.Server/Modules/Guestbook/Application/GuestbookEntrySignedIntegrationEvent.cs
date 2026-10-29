using System;
using Stomme.EventBus;

namespace Stomme.Modules.Guestbook.Application
{
    /// <summary>
    /// The shipped demo PRODUCER: the first real integration event a stock module publishes over
    /// the bus. Raised by <see cref="SignGuestbookCommandHandler"/> AFTER the unit of work commits, so
    /// a subscriber can never observe an entry that was rolled back. Published only when the bus is enabled
    /// (<c>Messaging:Enabled</c>) — with messaging off no <c>IEventBus</c> is registered and the handler
    /// no-ops, so a stock install is byte-for-byte unaffected.
    /// </summary>
    /// <remarks>
    /// This is the ASYNC cross-context announcement; it deliberately coexists with the two IN-PROCESS
    /// fan-outs the handler already fires (the <c>GuestbookEntrySignedDomainEvent</c> through the framework
    /// dispatcher and the <c>GuestbookSignedNotification</c> through the mediator) — the bus is for
    /// other processes/nodes, not a replacement for either.
    /// </remarks>
    public sealed record GuestbookEntrySignedIntegrationEvent(Guid EntryId, int ModuleId, string Author) : IntegrationEvent;
}
