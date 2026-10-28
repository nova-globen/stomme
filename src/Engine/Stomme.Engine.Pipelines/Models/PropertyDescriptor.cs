using System.Reflection;
using System.Text.Json.Serialization;

namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// A base type for <see cref="InputDescriptor"/> and <see cref="OutputDescriptor"/>.
/// </summary>
public abstract class PropertyDescriptor
{
    /// <summary>
    /// The name.
    /// </summary>
    public string Name { get; set; } = null!;

    /// <summary>
    /// The .NET type.
    /// </summary>
    [JsonPropertyName("typeName")]
    public Type Type { get; set; } = null!;

    /// <summary>
    /// The user friendly name of the input. Used by UI tools.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// The user friendly description of the input. Used by UI tools.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The order in which this input should be displayed by UI tools.
    /// </summary>
    public float Order { get; set; }

    /// <summary>
    /// True if this property should be displayed by UI tools, false otherwise.
    /// </summary>
    public bool? IsBrowsable { get; set; } = true;
    
    /// <summary>
    /// True if this property can be serialized.
    /// </summary>
    public bool? IsSerializable { get; set; }

    /// <summary>
    /// True if this input property is synthetic, which means it does not exist physically on the step's .NET type.
    /// </summary>
    public bool IsSynthetic { get; set; }
    
    /// <summary>
    /// Returns the value of the input property for the specified step.
    /// </summary>
    [JsonIgnore]
    public Func<IStep, object?> ValueGetter { get; set; } = null!;
    
    /// <summary>
    /// Sets the value of the input property for the specified step.
    /// </summary>
    [JsonIgnore]
    public Action<IStep, object?> ValueSetter { get; set; } = null!;
    
    /// <summary>
    /// The source of the property, if any.
    /// </summary>
    [JsonIgnore]
    public PropertyInfo? PropertyInfo { get; set; }
}