using System.Text.Json;
using System.Text.Json.Serialization;
using Stomme.Persistence.Abstractions;
using Stomme.SharedKernel.Domain;

namespace Stomme.Persistence;

/// <summary>
/// <see cref="IDomainEventSerializer"/> over System.Text.Json. Value objects in events (which expose
/// only private constructors) round-trip via <see cref="JsonConverter"/>s contributed through DI by
/// the owning contexts (e.g. tenancy registers converters for TenantId).
/// </summary>
internal sealed class DomainEventSerializer : IDomainEventSerializer
{
    private readonly DomainEventTypeRegistry _registry;
    private readonly JsonSerializerOptions _options;

    public DomainEventSerializer(DomainEventTypeRegistry registry, IEnumerable<JsonConverter> converters)
    {
        _registry = registry;
        _options = new JsonSerializerOptions();
        foreach (var converter in converters)
        {
            _options.Converters.Add(converter);
        }
    }

    public (string EventType, string Payload) Serialize(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var type = domainEvent.GetType();
        return (DomainEventTypeRegistry.KeyFor(type), JsonSerializer.Serialize(domainEvent, type, _options));
    }

    public IDomainEvent? Deserialize(string eventType, string payload)
    {
        var type = _registry.Resolve(eventType);
        return type is null ? null : (IDomainEvent?)JsonSerializer.Deserialize(payload, type, _options);
    }
}
