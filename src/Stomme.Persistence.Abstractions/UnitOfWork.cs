using Stomme.SharedKernel.Domain;

namespace Stomme.Persistence.Abstractions;

/// <summary>
/// The framework's single <see cref="IUnitOfWork"/>. The store providers persist each aggregate eagerly
/// and enroll it in the ambient <see cref="IAggregateTracker"/> on every repository call, so this unit of
/// work's remaining responsibility is to drain the aggregates written during one logical operation,
/// dispatch their domain events to the in-scope handlers, and clear them. Call
/// <see cref="SaveChangesAsync"/> after the writes that make up one operation. It is what drives the
/// FeatureManagement flag-change audit trail: the audit handler stages its row inside this dispatch, in
/// the caller's scope (docs/decisions.md D40).
/// </summary>
/// <remarks>
/// <para>
/// ONE IMPLEMENTATION, DELIBERATELY CLEAR-BEFORE-DISPATCH (docs/decisions.md D41 / D67). This type
/// collapsed two rivals that both registered via <c>TryAddScoped</c> and so let registration order decide
/// the semantics — <c>Stomme.Persistence.UnitOfWork</c> (snapshot, clear, then dispatch) and
/// <c>Stomme.FeatureManagement.Application.FeatureUnitOfWork</c> (dispatch, then clear). It lives in
/// <c>Stomme.Persistence.Abstractions</c> — the one assembly both <c>AddDomainEvents</c> and
/// <c>AddFeatureManagement</c> reference — so every composition root now registers this SAME type and the
/// order-dependence is gone, exactly as D40 did for <see cref="IDomainEventDispatcher"/> one line away.
/// </para>
/// <para>
/// The surviving order is <c>UnitOfWork</c>'s clear-before-dispatch: snapshot the events, clear every
/// aggregate, THEN dispatch. This is the re-entrancy-safe order — a handler that writes more aggregates
/// starts a fresh tracking generation instead of re-dispatching the events already in flight. It is
/// observationally identical to the retired dispatch-before-clear order for the FeatureManagement audit,
/// whose handler stages an audit row and never re-raises an event on a dequeued aggregate; only the unused
/// return value differed (event count vs a constant 0).
/// </para>
/// </remarks>
public sealed class UnitOfWork(IAggregateTracker tracker, IDomainEventDispatcher dispatcher) : IUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var aggregates = tracker.DequeueTracked();
        if (aggregates.Count == 0)
        {
            return 0;
        }

        // Snapshot then clear before dispatch, so a handler that writes more aggregates starts a
        // fresh tracking generation rather than re-dispatching these events.
        var events = aggregates.SelectMany(a => a.DomainEvents).ToArray();
        foreach (var aggregate in aggregates)
        {
            aggregate.ClearDomainEvents();
        }

        await dispatcher.DispatchAsync(events, ct);
        return events.Length;
    }

    public void Dispose()
    {
        // No unmanaged or scoped state to release; the tracker is ambient and self-clearing.
    }
}
