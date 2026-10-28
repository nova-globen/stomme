using Stomme.SharedKernel.Domain;

namespace Stomme.Persistence.Abstractions;

/// <summary>
/// Collects aggregates that have been written during the current logical operation so the unit of
/// work can dispatch their domain events. Repositories enroll aggregates on add/update; the unit of
/// work dequeues them on save. Implementations are ambient (flow-scoped), so they compose with the
/// single-instance repositories.
/// </summary>
public interface IAggregateTracker
{
    /// <summary>Enrolls an aggregate whose domain events should be dispatched on the next save.</summary>
    void Track(IHasDomainEvents aggregate);

    /// <summary>Returns the tracked aggregates for the current flow and clears the tracking set.</summary>
    IReadOnlyCollection<IHasDomainEvents> DequeueTracked();
}
