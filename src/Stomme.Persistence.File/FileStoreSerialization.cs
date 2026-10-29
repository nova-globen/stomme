using System.Collections.Concurrent;
using System.Reflection;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Stomme.Persistence.File;

/// <summary>
/// The Newtonsoft settings the file store round-trips aggregates with — the SAME serialization approach the
/// RavenDB store uses (<c>Stomme.Persistence.RavenDb.ValueObjectJsonConverters</c> /
/// <c>DomainEventsIgnoringContractResolver</c>): a scalar converter for <see cref="IStringValueObject"/> /
/// <see cref="IMapValueObject"/>, a scalar <see cref="TenantId"/> converter, the aggregate's transient
/// <c>DomainEvents</c> collection ignored, and non-public constructors bound so a private-ctor aggregate and a
/// private-ctor value object reconstruct. It is deliberately kept OUT of the RavenDB assembly (whose converters
/// are internal to it) so the file provider is self-contained, but it is contract-identical: a document written
/// by one store reconstructs through the other. System.Text.Json is NOT used here — it cannot invoke a private
/// parameterless constructor nor a value object's static factory, so it would silently fail to rebuild the
/// aggregate (the risk the D80 brief flagged; driven green via the conformance suite).
/// </summary>
internal static class FileStoreSerializer
{
    private static readonly JsonSerializerSettings Settings = Build();

    private static JsonSerializerSettings Build()
    {
        var settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            ContractResolver = new FileStoreContractResolver(),
            // The aggregate roots expose only a private parameterless constructor (EF/Raven materialisation
            // ctor); allow Newtonsoft to use it, then fill the private property setters as it does by default.
            ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
            // Preserve DateTimeKind so a UTC SignedOnUtc reads back UTC (deterministic newest-first ordering).
            DateTimeZoneHandling = DateTimeZoneHandling.RoundtripKind,
            NullValueHandling = NullValueHandling.Ignore,
        };

        settings.Converters.Add(new TenantIdConverter());
        settings.Converters.Add(new StringValueObjectConverter());
        settings.Converters.Add(new MapValueObjectConverter());
        return settings;
    }

    public static string Serialize(object aggregate) => JsonConvert.SerializeObject(aggregate, Settings);

    public static T? Deserialize<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);

    /// <summary>
    /// A fingerprint of what this serializer WRITES for <paramref name="types"/>: every serialized property's name
    /// and type, and those of every object it holds, recursively; a value a converter writes as a scalar counts as its type
    /// alone. A property added, removed, renamed or retyped changes it; a method, an ignored member, or the order the types
    /// are given in does not. <paramref name="also"/> adds what else the data depends on (the framework part's migration
    /// ids). A backup part carries it as its schema version, so a restore compares shapes rather than version numbers.
    /// </summary>
    public static string Shape(IEnumerable<Type> types, IEnumerable<string>? also = null)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ShapeText(types, also)));
        return "shape-" + Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
    }

    /// <summary>What <see cref="Shape"/> hashes - readable, for the tests that pin it.</summary>
    internal static string ShapeText(IEnumerable<Type> types, IEnumerable<string>? also = null)
    {
        var text = new System.Text.StringBuilder();
        var described = new HashSet<Type>();
        foreach (var type in types.Distinct().OrderBy(type => Name(type), StringComparer.Ordinal))
        {
            Describe(type, text, described);
            text.Append('\n');
        }

        foreach (var item in (also ?? Enumerable.Empty<string>()).OrderBy(item => item, StringComparer.Ordinal))
        {
            text.Append(item).Append('\n');
        }

        return text.ToString();
    }

    private static void Describe(Type type, System.Text.StringBuilder text, HashSet<Type> described)
    {
        text.Append(Name(type));
        if (!described.Add(type) || Settings.Converters.Any(converter => converter.CanConvert(type)))
        {
            return;
        }

        switch (Settings.ContractResolver!.ResolveContract(type))
        {
            case JsonObjectContract contract:
                text.Append('{');
                foreach (var property in contract.Properties.Where(property => !property.Ignored).OrderBy(property => property.PropertyName, StringComparer.Ordinal))
                {
                    text.Append(property.PropertyName).Append(':');
                    Describe(property.PropertyType ?? typeof(object), text, described);
                    text.Append(';');
                }

                text.Append('}');
                break;
            case JsonDictionaryContract contract:
                text.Append('<');
                Describe(contract.DictionaryKeyType ?? typeof(object), text, described);
                text.Append(',');
                Describe(contract.DictionaryValueType ?? typeof(object), text, described);
                text.Append('>');
                break;
            case JsonArrayContract contract:
                text.Append('[');
                Describe(contract.CollectionItemType ?? typeof(object), text, described);
                text.Append(']');
                break;
        }
    }

    private static string Name(Type type) =>
        type.IsGenericType
            ? $"{type.Namespace}.{type.Name}[{string.Join(",", type.GetGenericArguments().Select(Name))}]"
            : type.FullName ?? type.Name;
}

/// <summary>
/// Contract resolver mirroring the RavenDB seam's <c>DomainEventsIgnoringContractResolver</c>, on the plain
/// <see cref="DefaultContractResolver"/> base: it never persists an aggregate's transient <c>DomainEvents</c>
/// collection, and it binds a single non-public parameterized constructor for a value object whose only usable
/// constructor is private (so a multi-property VO round-trips). Types that HAVE a parameterless constructor (the
/// aggregates) keep Newtonsoft's construct-then-set path, enabled by
/// <see cref="ConstructorHandling.AllowNonPublicDefaultConstructor"/>.
/// </summary>
internal sealed class FileStoreContractResolver : DefaultContractResolver
{
    protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
    {
        var property = base.CreateProperty(member, memberSerialization);

        if (member.Name == nameof(IHasDomainEvents.DomainEvents)
            && typeof(IHasDomainEvents).IsAssignableFrom(member.DeclaringType))
        {
            property.Ignored = true;
            property.ShouldSerialize = _ => false;
            return property;
        }

        // The aggregates and value objects expose PRIVATE/PROTECTED property setters (Id, ModuleId, Author,
        // Message, ...). Newtonsoft's DefaultContractResolver leaves such a property non-Writable, so a loaded
        // document reads back with every property at its default (0/null) — the deserialisation failure D80
        // drove out. Force Writable when a non-public setter exists so the deserializer populates it (the
        // RavenDB seam's resolver does the same; EF materialises through the setter directly).
        if (!property.Writable && member is PropertyInfo { } propertyInfo)
        {
            property.Writable = propertyInfo.GetSetMethod(nonPublic: true) is not null;
        }

        return property;
    }

    protected override JsonObjectContract CreateObjectContract(Type objectType)
    {
        var contract = base.CreateObjectContract(objectType);
        if (contract.OverrideCreator is null && contract.CreatorParameters.Count == 0)
        {
            var constructor = SelectNonPublicValueConstructor(objectType);
            if (constructor is not null)
            {
                contract.OverrideCreator = arguments => constructor.Invoke(arguments);
                foreach (var parameter in CreateConstructorParameters(constructor, contract.Properties))
                {
                    contract.CreatorParameters.Add(parameter);
                }
            }
        }

        return contract;
    }

    // The sole non-public parameterized constructor of a private-ctor value object, or null when the type is
    // not that shape (it has a parameterless ctor — leave Newtonsoft's default path — or does not have exactly
    // one non-record parameterized non-public ctor). Identical rule to the RavenDB resolver.
    private static ConstructorInfo? SelectNonPublicValueConstructor(Type objectType)
    {
        var constructors = objectType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (constructors.Any(constructor => constructor.GetParameters().Length == 0))
        {
            return null;
        }

        var candidates = constructors
            .Where(constructor => !constructor.IsPublic)
            .Where(constructor =>
            {
                var parameters = constructor.GetParameters();
                if (parameters.Length == 0)
                {
                    return false;
                }

                // Skip a record copy constructor: a lone parameter of the declaring type.
                return parameters is not [{ } single] || single.ParameterType != objectType;
            })
            .ToArray();

        return candidates.Length == 1 ? candidates[0] : null;
    }
}

/// <summary>Round-trips an <see cref="IStringValueObject"/> as a scalar JSON string (via its public static Create).</summary>
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

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer) =>
        reader.TokenType == JsonToken.Null ? null
        : reader.Value?.ToString() is { } raw ? Factory(objectType)(raw)
        : null;

    private static Func<string, object> Factory(Type type) => Factories.GetOrAdd(type, static t =>
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

    private static Func<IReadOnlyDictionary<string, string>, object> Factory(Type type) => Factories.GetOrAdd(type, static t =>
    {
        var method = t.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, [typeof(IReadOnlyDictionary<string, string>)])
            ?? throw new InvalidOperationException($"{t.Name} must expose a public static Create(IReadOnlyDictionary<string, string>) to be persisted as an IMapValueObject.");
        return values => method.Invoke(null, [values])!;
    });
}

/// <summary>Serializes <see cref="TenantId"/> as a scalar GUID (its constructor is private) — mirrors the Raven converter.</summary>
internal sealed class TenantIdConverter : JsonConverter<TenantId>
{
    public override void WriteJson(JsonWriter writer, TenantId? value, JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        writer.WriteValue(value.Value);
    }

    public override TenantId? ReadJson(JsonReader reader, Type objectType, TenantId? existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }

        return reader.Value switch
        {
            null => null,
            Guid guid => TenantId.From(guid),
            var raw => TenantId.From(Guid.Parse(raw.ToString()!)),
        };
    }
}
