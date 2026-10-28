using Stomme.SharedKernel.Domain;

namespace Stomme.Persistence.Abstractions;

/// <summary>
/// Default <see cref="IAggregateTracker"/>: a simple collector registered <b>scoped</b>, so the
/// repositories that enroll aggregates and the unit of work that dispatches their events share one
/// instance per logical operation (request or background scope). It is deliberately not ambient
/// (<see cref="AsyncLocal{T}"/>): a mutation made inside an async repository call would not flow back
/// up to the caller, whereas a shared scoped instance is seen by everyone in the scope.
/// </summary>
public sealed class AggregateTracker : IAggregateTracker
{
    private readonly Lock _gate = new();
    private readonly List<IHasDomainEvents> _tracked = [];

    public void Track(IHasDomainEvents aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        lock (_gate)
        {
            if (!_tracked.Contains(aggregate))
            {
                _tracked.Add(aggregate);
            }
        }
    }

    public IReadOnlyCollection<IHasDomainEvents> DequeueTracked()
    {
        lock (_gate)
        {
            if (_tracked.Count == 0)
            {
                return [];
            }

            var snapshot = _tracked.ToArray();
            _tracked.Clear();
            return snapshot;
        }
    }
}
