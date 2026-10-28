using System.Text.Json;
using System.Text.Json.Serialization;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Serialization.Helpers;

namespace Stomme.Engine.Pipelines.Serialization.Converters;

/// <summary>
/// Serializes the <see cref="StepNode"/> type without its children. Instead, it will serialize the step children as properties.
/// </summary>
public class RootStepNodeConverter(StepWriter stepWriter, bool excludeChildren = false) : JsonConverter<StepNode>
{
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, StepNode value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("nodeId", value.NodeId);
        writer.WriteString("port", value.Port);
        writer.WritePropertyName("step");
        stepWriter.WriteStep(writer, value.Step, options, excludeChildren: excludeChildren);
        writer.WriteEndObject();
    }

    /// <inheritdoc />
    public override StepNode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }
}