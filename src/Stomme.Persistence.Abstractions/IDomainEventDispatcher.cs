using Stomme.SharedKernel.Domain;

namespace Stomme.Persistence.Abstractions;

/// <summary>
/// Dispatches domain events to their registered <see cref="IDomainEventHandler{TEvent}"/> handlers.
/// Invoked by the unit of work after the raising aggregates have been persisted.
/// </summary>
public interface IDomainEventDispatcher
{
    Task DispatchAsync(IReadOnlyCollection<IDomainEvent> domainEvents, CancellationToken ct = default);
}
