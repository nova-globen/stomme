using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Serialization.Helpers;

namespace Stomme.Engine.Pipelines.Serialization.Converters;

/// <summary>
/// Ignores properties with the <see cref="JsonIgnoreCompositeRootAttribute"/> attribute.
/// </summary>
public class JsonIgnoreCompositeRootConverter(StepWriter stepWriter) : JsonConverter<IStep>
{
    /// <inheritdoc />
    public override IStep Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc />
    [UnconditionalSuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "<Pending>")]
    public override void Write(Utf8JsonWriter writer, IStep? value, JsonSerializerOptions options)
    {
        stepWriter.WriteStep(writer, value, options, ignoreSpecializedConverters: true, propertyFilter: property => property.GetCustomAttribute<JsonIgnoreCompositeRootAttribute>() != null);
    }
}