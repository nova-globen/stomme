using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Stomme.Persistence.EfCore;

/// <summary>
/// The JSON COLLECTION convention of the shared <see cref="ModuleAggregatesDbContext"/> (BL-78 / D100 design
/// point 7). A public collection property of a storage-blind aggregate — <c>IReadOnlyList&lt;T&gt; X =&gt; _x;</c>
/// over a private backing list, whatever <c>T</c> is (enum, string, record, a record carrying a string value object,
/// an <see cref="Entity{TId}"/> child) — is mapped as ONE string column holding the collection's JSON, read and
/// written through the backing field, with a value comparer so an in-place mutation is detected.
/// <para>
/// <b>Why (MEASURED 2026-09-14, <c>ModuleAggregateCollectionMappingTests</c>, SQLite).</b> Without it the five
/// element shapes did three different things: an enum list and a string list were SILENTLY UNMAPPED (no column,
/// round-trip empty); an <see cref="Entity{TId}"/> child list became an EF NAVIGATION (a second table with a shadow
/// FK — EF-only, so the providers diverged in shape); a keyless record list THREW at model build ("requires a
/// primary key") and a record carrying a value object threw "No suitable constructor" — both swallowed by the
/// schema initializer as "it will read as empty".
/// </para>
/// <para>
/// <b>Serialization contract.</b> The rules MIRROR <c>Stomme.Persistence.File.FileStoreSerializer</c> (itself
/// contract-identical to the RavenDB seam's): Newtonsoft; <see cref="IStringValueObject"/> / <see cref="IMapValueObject"/>
/// / <see cref="TenantId"/> as scalars; non-public setters writable; a single non-public parameterized constructor
/// bound; <see cref="ConstructorHandling.AllowNonPublicDefaultConstructor"/>; <see cref="DateTimeZoneHandling.RoundtripKind"/>;
/// <c>DomainEvents</c> never written. It is MIRRORED rather than shared because the File type is internal to its
/// assembly and hoisting Newtonsoft into <c>Stomme.Persistence.Abstractions</c> would impose it on every
/// consumer; the EF == Raven == File conformance suite (<c>CollectionBearingAggregateConformanceTests</c>) is what
/// holds the three together. The column is compact JSON (no indentation).
/// </para>
/// <para>
/// <b>Loud, never silent.</b> An element type the contract cannot reconstruct (no usable constructor), or a getter-
/// only collection with no backing field to write, throws <see cref="NotSupportedException"/> at model build.
/// </para>
/// </summary>
internal static class ModuleAggregateJsonCollections
{
    private static readonly MethodInfo ApplyMethod =
        typeof(ModuleAggregateJsonCollections).GetMethod(nameof(ApplyTyped), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// True for a property the convention owns: a generic <see cref="IEnumerable{T}"/> that is not a string, not a
    /// binary <c>byte[]</c>, not a value object (the existing value-object branches keep those), not
    /// <see cref="TenantId"/>, and not the aggregate's transient <c>DomainEvents</c>.
    /// </summary>
    public static bool IsJsonCollection(PropertyInfo property)
    {
        var type = property.PropertyType;
        if (type == typeof(string) || type == typeof(byte[]) || type == typeof(TenantId)
            || typeof(IStringValueObject).IsAssignableFrom(type)
            || typeof(IMapValueObject).IsAssignableFrom(type)
            || typeof(ValueObject).IsAssignableFrom(type))
        {
            return false;
        }

        if (property.Name == nameof(IHasDomainEvents.DomainEvents)
            && typeof(IHasDomainEvents).IsAssignableFrom(property.DeclaringType))
        {
            return false;
        }

        return ElementTypeOf(type) is not null;
    }

    /// <summary>Maps <paramref name="property"/> of <paramref name="aggregate"/> as one JSON column; returns the element type.</summary>
    public static System.Type Apply(EntityTypeBuilder entity, System.Type aggregate, PropertyInfo property)
    {
        var elementType = ElementTypeOf(property.PropertyType)!;
        EnsureSerializable(aggregate, property, elementType);

        var backingField = FindBackingField(aggregate, property);
        if (backingField is null && property.GetSetMethod(nonPublic: true) is null)
        {
            throw new NotSupportedException(
                $"The shared module store cannot map collection property '{aggregate.Name}.{property.Name}': it has no " +
                "setter and no backing field (expected a private field named '_" + char.ToLowerInvariant(property.Name[0]) +
                property.Name[1..] + "' whose type is assignable to the property type), so a loaded aggregate could not " +
                "be given its collection.");
        }

        ApplyMethod.MakeGenericMethod(property.PropertyType).Invoke(null, [entity, property, backingField]);
        return elementType;
    }

    private static void ApplyTyped<TCollection>(EntityTypeBuilder entity, PropertyInfo property, FieldInfo? backingField)
        where TCollection : class
    {
        // Materialize the BACKING FIELD's concrete type (List<T>), not the interface: Newtonsoft turns an
        // IReadOnlyList<T> into a ReadOnlyCollection<T>, which EF cannot assign to a List<T> field (MEASURED:
        // InvalidCastException on first read).
        var materializeAs = backingField is { FieldType: { IsInterface: false, IsAbstract: false } fieldType }
            ? fieldType
            : typeof(TCollection);

        var converter = new ValueConverter<TCollection, string>(
            value => ModuleAggregateJsonSerializer.Serialize(value),
            json => (TCollection)ModuleAggregateJsonSerializer.Deserialize(json, materializeAs)!);

        // Compare by the serialized form and snapshot by a round-trip, so adding to / removing from the SAME list
        // instance is a detected change (reference equality would miss it and EF would write nothing).
        var comparer = new ValueComparer<TCollection>(
            (left, right) => ModuleAggregateJsonSerializer.Serialize(left) == ModuleAggregateJsonSerializer.Serialize(right),
            value => ModuleAggregateJsonSerializer.Serialize(value).GetHashCode(StringComparison.Ordinal),
            value => (TCollection)ModuleAggregateJsonSerializer.Deserialize(ModuleAggregateJsonSerializer.Serialize(value), materializeAs)!);

        var builder = entity.Property(typeof(TCollection), property.Name).HasConversion(converter, comparer);
        if (backingField is not null)
        {
            builder.HasField(backingField.Name).UsePropertyAccessMode(PropertyAccessMode.Field);
        }
    }

    private static System.Type? ElementTypeOf(System.Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return type.GetGenericArguments()[0];
        }

        return type.GetInterfaces()
            .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))?
            .GetGenericArguments()[0];
    }

    // The EF backing-field naming convention (_items / _Items / m_items / items), restricted to a private instance
    // field whose type can hold what the converter materializes and be read back as the property type.
    private static FieldInfo? FindBackingField(System.Type aggregate, PropertyInfo property)
    {
        var camel = char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
        string[] names = ["_" + camel, "_" + property.Name, "m_" + camel, camel];
        for (var type = aggregate; type is not null; type = type.BaseType)
        {
            foreach (var name in names)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field is not null && property.PropertyType.IsAssignableFrom(field.FieldType))
                {
                    return field;
                }
            }
        }

        return null;
    }

    // Refuse, at model build, an element the contract cannot reconstruct — so the failure is a loud
    // NotSupportedException naming the property, not a runtime JsonSerializationException on first read (or a
    // silent empty read through the initializer's catch).
    private static void EnsureSerializable(System.Type aggregate, PropertyInfo property, System.Type elementType)
    {
        var problem = ModuleAggregateJsonSerializer.FindUnconstructibleType(elementType);
        if (problem is not null)
        {
            throw new NotSupportedException(
                $"The shared module store cannot map collection property '{aggregate.Name}.{property.Name}': its element " +
                $"shape '{problem.Name}' cannot be reconstructed from JSON (it needs a parameterless constructor, a single " +
                "public parameterized constructor, or a single non-public parameterized constructor whose parameters " +
                "match its properties; a value object must implement IStringValueObject or IMapValueObject).");
        }
    }
}

/// <summary>
/// The Newtonsoft settings the JSON collection column uses — see <see cref="ModuleAggregateJsonCollections"/> for
/// why it mirrors the File store's serializer rule for rule.
/// </summary>
internal static class ModuleAggregateJsonSerializer
{
    private static readonly ModuleAggregateJsonContractResolver Resolver = new();

    private static readonly JsonSerializerSettings Settings = Build();

    private static JsonSerializerSettings Build()
    {
        var settings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            ContractResolver = Resolver,
            ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor,
            DateTimeZoneHandling = DateTimeZoneHandling.RoundtripKind,
            NullValueHandling = NullValueHandling.Ignore,
        };

        settings.Converters.Add(new JsonTenantIdConverter());
        settings.Converters.Add(new JsonStringValueObjectConverter());
        settings.Converters.Add(new JsonMapValueObjectConverter());
        return settings;
    }

    public static string Serialize(object? value) => JsonConvert.SerializeObject(value, Settings);

    public static object? Deserialize(string json, System.Type type) => JsonConvert.DeserializeObject(json, type, Settings);

    /// <summary>
    /// The first type reachable from <paramref name="root"/> (itself, or a property of it, recursively) that the
    /// contract has no way to construct, or null when every object type in the shape is constructible.
    /// </summary>
    public static System.Type? FindUnconstructibleType(System.Type root)
    {
        var visited = new HashSet<System.Type>();
        return Visit(root);

        System.Type? Visit(System.Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (!visited.Add(type) || type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
                || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)
                || type == typeof(Guid) || type == typeof(object))
            {
                return null;
            }

            if (Settings.Converters.Any(converter => converter.CanConvert(type)))
            {
                return null;
            }

            var contract = Resolver.ResolveContract(type);
            switch (contract)
            {
                case JsonArrayContract array when array.CollectionItemType is not null:
                    return Visit(array.CollectionItemType);
                case JsonDictionaryContract dictionary:
                    return (dictionary.DictionaryKeyType is { } key ? Visit(key) : null)
                        ?? (dictionary.DictionaryValueType is { } value ? Visit(value) : null);
                case JsonObjectContract objectContract:
                    if (type.IsAbstract || type.IsInterface
                        || (objectContract.DefaultCreator is null
                            && objectContract.OverrideCreator is null
                            && objectContract.CreatorParameters.Count == 0))
                    {
                        return type;
                    }

                    // An unmarked value object inside an element is the same unsupported shape the scalar branch
                    // rejects: its private constructor round-trip is fragile, the marker is the sanctioned path.
                    if (typeof(ValueObject).IsAssignableFrom(type))
                    {
                        return type;
                    }

                    foreach (var member in objectContract.Properties.Where(member => !member.Ignored && member.PropertyType is not null))
                    {
                        if (Visit(member.PropertyType!) is { } problem)
                        {
                            return problem;
                        }
                    }

                    return null;
                default:
                    return null;
            }
        }
    }
}

/// <summary>
/// Mirror of <c>Stomme.Persistence.File.FileStoreContractResolver</c>: never writes <c>DomainEvents</c>, makes a
/// property with a non-public setter writable, and binds a single non-public parameterized constructor.
/// </summary>
internal sealed class ModuleAggregateJsonContractResolver : DefaultContractResolver
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

        if (!property.Writable && member is PropertyInfo { } propertyInfo)
        {
            property.Writable = propertyInfo.GetSetMethod(nonPublic: true) is not null;
        }

        return property;
    }

    protected override JsonObjectContract CreateObjectContract(System.Type objectType)
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

    private static ConstructorInfo? SelectNonPublicValueConstructor(System.Type objectType)
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

                return parameters is not [{ } single] || single.ParameterType != objectType;
            })
            .ToArray();

        return candidates.Length == 1 ? candidates[0] : null;
    }
}

/// <summary>Round-trips an <see cref="IStringValueObject"/> as a scalar JSON string (via its public static Create).</summary>
internal sealed class JsonStringValueObjectConverter : JsonConverter
{
    private static readonly ConcurrentDictionary<System.Type, Func<string, object>> Factories = new();

    public override bool CanConvert(System.Type objectType) =>
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

    public override object? ReadJson(JsonReader reader, System.Type objectType, object? existingValue, JsonSerializer serializer) =>
        reader.TokenType == JsonToken.Null ? null
        : reader.Value?.ToString() is { } raw ? Factory(objectType)(raw)
        : null;

    private static Func<string, object> Factory(System.Type type) => Factories.GetOrAdd(type, static t =>
    {
        var method = t.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, [typeof(string)])
            ?? throw new InvalidOperationException($"{t.Name} must expose a public static Create(string) to be persisted as an IStringValueObject.");
        return raw => method.Invoke(null, [raw])!;
    });
}

/// <summary>Round-trips an <see cref="IMapValueObject"/> as a JSON object of string to string.</summary>
internal sealed class JsonMapValueObjectConverter : JsonConverter
{
    private static readonly ConcurrentDictionary<System.Type, Func<IReadOnlyDictionary<string, string>, object>> Factories = new();

    public override bool CanConvert(System.Type objectType) =>
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

    public override object? ReadJson(JsonReader reader, System.Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
        {
            return null;
        }

        var values = serializer.Deserialize<Dictionary<string, string>>(reader) ?? [];
        return Factory(objectType)(values);
    }

    private static Func<IReadOnlyDictionary<string, string>, object> Factory(System.Type type) => Factories.GetOrAdd(type, static t =>
    {
        var method = t.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, [typeof(IReadOnlyDictionary<string, string>)])
            ?? throw new InvalidOperationException($"{t.Name} must expose a public static Create(IReadOnlyDictionary<string, string>) to be persisted as an IMapValueObject.");
        return values => method.Invoke(null, [values])!;
    });
}

/// <summary>Serializes <see cref="TenantId"/> as a scalar GUID (its constructor is private).</summary>
internal sealed class JsonTenantIdConverter : JsonConverter<TenantId>
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

    public override TenantId? ReadJson(JsonReader reader, System.Type objectType, TenantId? existingValue, bool hasExistingValue, JsonSerializer serializer)
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