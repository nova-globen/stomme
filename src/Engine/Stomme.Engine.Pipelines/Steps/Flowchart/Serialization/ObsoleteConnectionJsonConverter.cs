using System.Text.Json;
using System.Text.Json.Serialization;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Serialization;

/// <summary>
/// Converts <see cref="ObsoleteConnection"/> to and from JSON.
/// </summary>
[Obsolete("Use ConnectionJsonConverter instead.")]
public class ObsoleteConnectionJsonConverter : JsonConverter<ObsoleteConnection>
{
    private readonly IDictionary<string, IStep> _steps;

    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(ObsoleteConnection);

    /// <inheritdoc />
    public ObsoleteConnectionJsonConverter(IDictionary<string, IStep> steps)
    {
        _steps = steps;
    }

    /// <inheritdoc />
    public override ObsoleteConnection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (!JsonDocument.TryParseValue(ref reader, out var doc))
            throw new JsonException("Failed to parse JsonDocument");

        var sourceId = doc.RootElement.GetProperty("source").GetString()!;
        var targetId = doc.RootElement.GetProperty("target").GetString()!;
        var sourcePort = doc.RootElement.GetProperty("sourcePort").GetString()!;
        var targetPort = doc.RootElement.GetProperty("targetPort").GetString()!;

        var source = _steps.TryGetValue(sourceId, out var s) ? s : null!;
        var target = _steps.TryGetValue(targetId, out var t) ? t : null!;

        return new ObsoleteConnection(source, target, sourcePort, targetPort);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ObsoleteConnection value, JsonSerializerOptions options)
    {
        var (step, target, sourcePort, targetPort) = value;

        var model = new
        {
            Source = step.Id,
            Target = target.Id,
            SourcePort = sourcePort,
            TargetPort = targetPort
        };

        JsonSerializer.Serialize(writer, model, options);
    }
}