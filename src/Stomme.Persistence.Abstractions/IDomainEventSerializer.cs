using Stomme.SharedKernel.Domain;

namespace Stomme.Persistence.Abstractions;

/// <summary>
/// Serializes a domain event to a stable type key plus a JSON payload for outbox storage, and
/// rehydrates it for dispatch. When this service is registered, the store providers switch from
/// in-process dispatch to writing the transactional outbox.
/// </summary>
public interface IDomainEventSerializer
{
    (string EventType, string Payload) Serialize(IDomainEvent domainEvent);

    /// <summary>Rehydrates an event, or returns null when the type key is unknown to this process.</summary>
    IDomainEvent? Deserialize(string eventType, string payload);
}
