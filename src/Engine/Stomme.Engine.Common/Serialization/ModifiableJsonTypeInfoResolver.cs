using System.Text.Json.Serialization.Metadata;
using Stomme.Engine.Extensions;

namespace Stomme.Engine.Common.Serialization;

/// <summary>
/// A custom JSON type info resolver that allows private constructors to be used when deserializing JSON.
/// </summary>
public class ModifiableJsonTypeInfoResolver : DefaultJsonTypeInfoResolver
{
    /// <inheritdoc />
    public ModifiableJsonTypeInfoResolver(IEnumerable<Action<JsonTypeInfo>> modifiers)
    {
        Modifiers.AddRange(modifiers);
    }
}