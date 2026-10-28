using System.Reflection;
using Stomme.SharedKernel.Domain;

namespace Stomme.Persistence;

/// <summary>
/// Maps a stable string key (the event's full type name) to its <see cref="IDomainEvent"/> CLR type,
/// so the outbox serializer can rehydrate events without embedding assembly-qualified names. Built
/// once from the assemblies that contribute domain events and their handlers.
/// </summary>
public sealed class DomainEventTypeRegistry
{
    private readonly IReadOnlyDictionary<string, Type> _byKey;

    public DomainEventTypeRegistry(IEnumerable<Assembly> assemblies)
    {
        var map = new Dictionary<string, Type>(StringComparer.Ordinal);
        foreach (var type in assemblies.SelectMany(a => a.GetTypes())
                     .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IDomainEvent).IsAssignableFrom(t)))
        {
            map[KeyFor(type)] = type;
        }

        _byKey = map;
    }

    public static string KeyFor(Type eventType) => eventType.FullName ?? eventType.Name;

    public Type? Resolve(string key) => _byKey.TryGetValue(key, out var type) ? type : null;
}
