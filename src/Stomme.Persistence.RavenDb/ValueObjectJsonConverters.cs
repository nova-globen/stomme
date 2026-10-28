using System.Collections.Concurrent;
using System.Reflection;
using Stomme.SharedKernel.Domain;
using Newtonsoft.Json;

namespace Stomme.Persistence.RavenDb;

// Convention-based converters that round-trip any value object implementing the SharedKernel serialization
// markers, so a module can persist its own value objects in RavenDB without this generic store referencing
// the module. Each reconstructs the value object through its public static Create factory (cached per type).

/// <summary>Round-trips an <see cref="IStringValueObject"/> as a scalar JSON string.</summary>
internal sealed class StringValueObjectConverter : JsonConverter
{
    private static readonly ConcurrentDictionary<Type, Func<string, object>> Factories = new();

    public override bool CanConvert(Type objectType) =>
        typeof(IStringValueObject).IsAssignableFrom(objectType) && objectType is { IsInterface: false, IsAbstract: false };

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteValue(((IStringValueObject)value).Value);
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }

        return reader.Value?.ToString() is { } raw ? Factory(objectType)(raw) : null;
    }

    private static Func<string, object> Factory(Type type) => Factories.GetOrAdd(type, t =>
    {
        var method = t.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, [typeof(string)])
            ?? throw new InvalidOperationException($"{t.Name} must expose a public static Create(string) to be persisted as an IStringValueObject.");
        return raw => method.Invoke(null, [raw])!;
    });
}

/// <summary>Round-trips an <see cref="IMapValueObject"/> as a JSON object of string→string.</summary>
internal sealed class MapValueObjectConverter : JsonConverter
{
    private static readonly ConcurrentDictionary<Type, Func<IReadOnlyDictionary<string, string>, object>> Factories = new();

    public override bool CanConvert(Type objectType) =>
        typeof(IMapValueObject).IsAssignableFrom(objectType) && objectType is { IsInterface: false, IsAbstract: false };

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        // The values are a plain dictionary (not a marker type), so this does not recurse into this converter.
        serializer.Serialize(writer, ((IMapValueObject)value).Values);
    }

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }

        var values = serializer.Deserialize<Dictionary<string, string>>(reader) ?? [];
        return Factory(objectType)(values);
    }

    private static Func<IReadOnlyDictionary<string, string>, object> Factory(Type type) => Factories.GetOrAdd(type, t =>
    {
        var method = t.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, [typeof(IReadOnlyDictionary<string, string>)])
            ?? throw new InvalidOperationException($"{t.Name} must expose a public static Create(IReadOnlyDictionary<string, string>) to be persisted as an IMapValueObject.");
        return values => method.Invoke(null, [values])!;
    });
}
