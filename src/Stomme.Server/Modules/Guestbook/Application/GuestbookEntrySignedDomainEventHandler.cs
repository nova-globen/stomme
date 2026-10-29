using System.Threading;
using System.Threading.Tasks;
using Stomme.Modules.Guestbook.Domain;
using Stomme.SharedKernel.Domain;
using Microsoft.Extensions.Logging;

namespace Stomme.Modules.Guestbook.Application
{
    /// <summary>
    /// The FRAMEWORK domain-event handler for <see cref="GuestbookEntrySignedDomainEvent"/>, dispatched by
    /// <c>CurrentScopeDomainEventDispatcher</c> when <c>IUnitOfWork.SaveChangesAsync</c> drains the aggregate
    /// (docs/decisions.md D40) — the SAME mechanism FeatureManagement's audit trail rides, and DISTINCT from
    /// the mediator's <c>GuestbookSignedNotification</c>. Having both fire on one signature is the point:
    /// D74 demonstrates the two in-process paths coexisting rather than collapsing them.
    /// <para>
    /// It obeys the handler contract: it stages/observes only — no <c>SaveChanges</c>, no new DI scope,
    /// no disposing the ambient session. <c>DomainEventHandlerContractTests</c> enforces that at build time.
    /// </para>
    /// </summary>
    public sealed class GuestbookEntrySignedDomainEventHandler(
        ILogger<GuestbookEntrySignedDomainEventHandler> logger)
        : IDomainEventHandler<GuestbookEntrySignedDomainEvent>
    {
        public Task HandleAsync(GuestbookEntrySignedDomainEvent domainEvent, CancellationToken ct = default)
        {
            logger.LogInformation(
                "[Guestbook] FRAMEWORK domain event: {Author} signed guestbook module {ModuleId} (entry {EntryId}).",
                domainEvent.Author, domainEvent.ModuleId, domainEvent.EntryId);
            return Task.CompletedTask;
        }
    }
}
