namespace Stomme.SharedKernel.Domain;

/// <summary>
/// Handles a domain event after the aggregate that raised it has been persisted. Handlers are
/// resolved and invoked by the unit of work via the domain-event dispatcher. Domain events are
/// internal to a bounded context; cross-context propagation uses the integration event bus instead.
/// </summary>
/// <remarks>
/// <para>
/// THE HANDLER CONTRACT — STAGE WORK, NEVER MANAGE IT.
/// </para>
/// <para>
/// A handler runs in the <b>caller's DI scope</b>, inside the caller's unit of work — that is the whole
/// point (domain events are internal to one bounded context, and the audit trail is meant to be atomic
/// with the change it audits). The cost of sharing that unit of work is re-entrancy: anything a handler does
/// to the ambient session is done to its caller's session, mid-operation. So:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>Do NOT call</b> <c>SaveChanges</c>/<c>SaveChangesAsync</c>, or commit the ambient session or
///     transaction. It flushes the OUTER unit of work's pending changes — including work the caller had not
///     finished staging, and including work the caller may still roll back.
///   </item>
///   <item>
///     <b>Do NOT create a DI scope</b> (<c>CreateScope</c>/<c>CreateAsyncScope</c>). A child scope gets a
///     different session and a re-resolved tenant context, which puts your write outside the aggregate's
///     transaction and re-opens the wrong-tenant class this contract exists to close.
///   </item>
///   <item>
///     <b>Do NOT dispose</b> the ambient session, context, or scope. You did not open it.
///   </item>
///   <item>
///     <b>DO stage</b>: add/update through the injected repository and return. The caller's
///     <c>IUnitOfWork.SaveChangesAsync</c> is what will commit your work together with the change that raised
///     the event. <b>Today it does not</b>: there is no transaction in the seam, repositories save per call,
///     and <c>SaveChangesAsync</c> only dispatches events (a transactional unit of work is the planned fix).
///     Write to this contract anyway — it is what makes that fix a change to the seam rather than to every handler.
///   </item>
/// </list>
/// <para>
/// Work that genuinely must land outside the aggregate's transaction is an <b>integration</b> event, not a
/// domain event: publish it over <c>IEventBus</c> and let the outbox bridge it.
/// </para>
/// <para>
/// This is a gate, not a suggestion: <c>DomainEventHandlerContractTests</c> parses every handler in the
/// framework and fails the build on a violation. A rule that is only written down is a rule that gets
/// broken.
/// </para>
/// </remarks>
public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken ct = default);
}
