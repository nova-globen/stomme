using System.Reflection;
using System.Text.Json.Serialization;

namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// A descriptor of a step's output property.
/// </summary>
public class OutputDescriptor : PropertyDescriptor
{
    /// <inheritdoc />
    [JsonConstructor]
    public OutputDescriptor()
    {
    }

    /// <inheritdoc />
    public OutputDescriptor(
        string name,
        string displayName,
        Type type,
        Func<IStep, object?> valueGetter,
        Action<IStep, object?> valueSetter,
        PropertyInfo? propertyInfo = default,
        string? description = default,
        bool? isBrowsable = default,
        bool? isSerializable = default)
    {
        Name = name;
        DisplayName = displayName;
        Type = type;
        ValueGetter = valueGetter;
        ValueSetter = valueSetter;
        Description = description;
        IsBrowsable = isBrowsable;
        IsSerializable = isSerializable;
        PropertyInfo = propertyInfo;
    }
}