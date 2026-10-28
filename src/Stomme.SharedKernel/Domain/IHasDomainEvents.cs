namespace Stomme.SharedKernel.Domain;

/// <summary>
/// Exposes the domain events an entity has raised, non-generically, so persistence infrastructure
/// (aggregate trackers, the unit of work) can collect and clear them without knowing the entity's
/// id type. Implemented by <see cref="Entity{TId}"/>.
/// </summary>
public interface IHasDomainEvents
{
    /// <summary>The domain events raised by this entity since it was loaded or last cleared.</summary>
    IReadOnlyList<IDomainEvent> DomainEvents { get; }

    /// <summary>Clears the raised domain events (called by the unit of work after dispatch).</summary>
    void ClearDomainEvents();
}
