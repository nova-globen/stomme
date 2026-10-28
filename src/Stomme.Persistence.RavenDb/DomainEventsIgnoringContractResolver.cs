using System.Reflection;
using Stomme.SharedKernel.Domain;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Raven.Client.Json.Serialization;
using Raven.Client.Json.Serialization.NewtonsoftJson;

namespace Stomme.Persistence.RavenDb;

/// <summary>
/// RavenDB contract resolver that never persists an aggregate's <c>DomainEvents</c> collection and
/// can reconstruct immutable value objects exposed only through a private parameterized constructor.
/// Domain events are transient signals dispatched by the unit of work after a save and then cleared;
/// they are not part of the stored document. Mirrors the EF Core context, which ignores the same
/// member.
/// </summary>
internal sealed class DomainEventsIgnoringContractResolver(ISerializationConventions conventions)
    : DefaultRavenContractResolver(conventions)
{
    protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
    {
        var property = base.CreateProperty(member, memberSerialization);
        if (member.Name == nameof(IHasDomainEvents.DomainEvents) &&
            typeof(IHasDomainEvents).IsAssignableFrom(member.DeclaringType))
        {
            property.Ignored = true;
            property.ShouldSerialize = _ => false;
        }

        return property;
    }

    /// <summary>
    /// Teaches Newtonsoft to deserialize composite value objects whose only usable constructor is
    /// non-public — the private-ctor + static-factory shape (e.g. <c>ResourceRef</c>). Newtonsoft
    /// ignores non-public constructors unless one carries <c>[JsonConstructor]</c>, so without this a
    /// loaded document cannot be materialized back into such a type. When the base resolver has not
    /// already selected a creator, we bind a single non-public parameterized constructor and map its
    /// parameters to the type's members.
    /// <para>
    /// This is deliberately narrow: it fires only for types that expose <em>no</em> parameterless
    /// constructor at all. A type Newtonsoft can instantiate parameterlessly (then fill via setters) is
    /// left entirely on its existing path — forcing a parameterized constructor there would drop any
    /// member the constructor doesn't cover. Scalar VOs (<c>TenantId</c>, <c>TenantSlug</c>) keep their
    /// dedicated converters; this only fills the gap for multi-property VOs where a converter would be
    /// heavier than needed.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Returns the sole non-public parameterized constructor of <paramref name="objectType"/> to bind
    /// for deserialization, or <c>null</c> when the type does not fit the private-ctor + static-factory
    /// pattern. The type must have <em>no</em> parameterless constructor (else Newtonsoft's default
    /// construct-then-set path is correct and must not be overridden), and exactly one non-public
    /// parameterized constructor once the record copy constructor (a lone parameter of the declaring
    /// type) is excluded.
    /// </summary>
    private static ConstructorInfo? SelectNonPublicValueConstructor(Type objectType)
    {
        var constructors = objectType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // If the type can be constructed with no arguments, Newtonsoft fills it via setters — leave it.
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

                // Skip the record copy constructor: a lone parameter of the declaring type.
                return parameters is not [{ } single] || single.ParameterType != objectType;
            })
            .ToArray();

        return candidates.Length == 1 ? candidates[0] : null;
    }
}
