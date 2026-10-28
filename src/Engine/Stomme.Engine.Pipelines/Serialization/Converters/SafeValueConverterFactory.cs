using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stomme.Engine.Pipelines.Serialization.Converters;

/// <summary>
/// A JSON converter factory that creates <see cref="SafeValueConverterFactory"/> instances.
/// </summary>
public class SafeValueConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => true;

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        return new SafeValueConverter();
    }
}