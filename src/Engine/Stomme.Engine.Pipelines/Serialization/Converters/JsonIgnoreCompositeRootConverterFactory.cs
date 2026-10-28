using System.Text.Json;
using System.Text.Json.Serialization;
using Stomme.Engine.Pipelines.Serialization.Helpers;

namespace Stomme.Engine.Pipelines.Serialization.Converters;

/// <summary>
/// A <see cref="JsonConverterFactory"/> that creates <see cref="JsonIgnoreCompositeRootConverter"/> instances.
/// </summary>
public class JsonIgnoreCompositeRootConverterFactory(StepWriter stepWriter) : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => typeof(IStep).IsAssignableFrom(typeToConvert);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        return new JsonIgnoreCompositeRootConverter(stepWriter);
    }
}