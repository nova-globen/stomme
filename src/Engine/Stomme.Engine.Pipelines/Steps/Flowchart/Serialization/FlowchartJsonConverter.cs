using System.Text.Json;
using System.Text.Json.Serialization;
using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps.Flowchart.Models;
using Stomme.Engine.Pipelines.Memory;
using Stomme.Engine.Pipelines.Serialization.Converters;
using JetBrains.Annotations;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Steps.Flowchart.Serialization;

/// <summary>
/// A JSON converter for <see cref="Steps.Flowchart"/>.
/// </summary>
[UsedImplicitly]
public class FlowchartJsonConverter(IIdentityGenerator identityGenerator, IWellKnownTypeRegistry wellKnownTypeRegistry, ILoggerFactory loggerFactory) : JsonConverter<Steps.Flowchart>
{
    private const string AllStepsKey = "allSteps";
    private const string AllConnectionsKey = "allConnections";
    private const string NotFoundConnectionsKey = "notFoundConnections";

    /// <inheritdoc />
    public override Steps.Flowchart Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (!JsonDocument.TryParseValue(ref reader, out var doc))
            throw new JsonException("Failed to parse JsonDocument");

        var id = doc.RootElement.TryGetProperty("id", out var idAttribute) ? idAttribute.GetString()! : identityGenerator.GenerateId();
        var nodeId = doc.RootElement.TryGetProperty("nodeId", out var nodeIdAttribute) ? nodeIdAttribute.GetString() : null;
        var name = doc.RootElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        var type = doc.RootElement.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
        var version = doc.RootElement.TryGetProperty("version", out var versionElement) ? versionElement.GetInt32() : 1;
        var runAsynchronously = doc.RootElement.TryGetProperty("runAsynchronously", out var runAsyncElement) && runAsyncElement.GetBoolean();

        var connectionsElement = doc.RootElement.TryGetProperty("connections", out var connectionsEl) ? connectionsEl : default;
        var stepsElement = doc.RootElement.TryGetProperty("steps", out var stepsEl) ? stepsEl : default;
        var steps = stepsElement.ValueKind != JsonValueKind.Undefined ? stepsElement.Deserialize<ICollection<IStep>>(options) ?? new List<IStep>() : new List<IStep>();
        var stepDictionary = steps.ToDictionary(x => x.Id);
        var connections = DeserializeConnections(connectionsElement, stepDictionary, options);
        var notFoundConnections = GetNotFoundConnections(doc.RootElement, stepDictionary, connections, options);
        var connectionsToRestore = FindConnectionsThatCanBeRestored(notFoundConnections, steps);
        var connectionComparer = new ConnectionComparer();
        var connectionsWithRestoredOnes = connections.Except(notFoundConnections, connectionComparer).Union(connectionsToRestore, connectionComparer).ToList();

        var variablesElement = doc.RootElement.TryGetProperty("variables", out var variablesEl) ? variablesEl : default;
        var variables = variablesElement.ValueKind != JsonValueKind.Undefined ? variablesElement.Deserialize<ICollection<Variable>>(options) ?? new List<Variable>() : new List<Variable>();

        var polymorphicOptions = options.Clone();
        polymorphicOptions.Converters.Add(new PolymorphicDictionaryConverter(options, wellKnownTypeRegistry));

        var metadataElement = doc.RootElement.TryGetProperty("metadata", out var metadataEl) ? metadataEl : default;
        var metadata = metadataElement.ValueKind != JsonValueKind.Undefined ? metadataElement.Deserialize<IDictionary<string, object>>(polymorphicOptions) ?? new Dictionary<string, object>() : new Dictionary<string, object>();

        var customPropertiesElement = doc.RootElement.TryGetProperty("customProperties", out var customPropertiesEl) ? customPropertiesEl : default;
        var customProperties = customPropertiesEl.ValueKind != JsonValueKind.Undefined ? customPropertiesElement.Deserialize<IDictionary<string, object>>(polymorphicOptions) ?? new Dictionary<string, object>() : new Dictionary<string, object>();
        customProperties[AllStepsKey] = steps.ToList();
        customProperties[AllConnectionsKey] = connectionsWithRestoredOnes;
        customProperties[NotFoundConnectionsKey] = notFoundConnections.Except(connectionsToRestore, connectionComparer).ToList();

        var flowChart = new Steps.Flowchart
        {
            Id = id,
            NodeId = nodeId!,
            Name = name,
            Type = type!,
            RunAsynchronously = runAsynchronously,
            Version = version,
            CustomProperties = customProperties,
            Metadata = metadata,
            Steps = steps,
            Variables = variables,
            Connections = connectionsWithRestoredOnes,
        };

        return flowChart;
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Steps.Flowchart value, JsonSerializerOptions options)
    {
        var steps = value.Steps;
        var stepDictionary = steps.ToDictionary(x => x.Id);

        var customProperties = new Dictionary<string, object>(value.CustomProperties);
        var allSteps = customProperties.GetValueOrDefault(AllStepsKey, steps);
        var allConnections = (ICollection<Connection>)(customProperties.TryGetValue(AllConnectionsKey, out var c) ? c : value.Connections);

        customProperties.Remove(AllStepsKey);
        customProperties.Remove(AllConnectionsKey);

        var model = new
        {
            value.Id,
            value.NodeId,
            value.Name,
            value.Type,
            value.Version,
            CustomProperties = customProperties,
            value.Metadata,
            Steps = allSteps,
            value.Variables,
            Connections = allConnections,
        };

        var flowchartSerializerOptions = new JsonSerializerOptions(options);
        flowchartSerializerOptions.Converters.Add(new ConnectionJsonConverter(stepDictionary, loggerFactory));
        flowchartSerializerOptions.Converters.Add(new PolymorphicDictionaryConverter(options, wellKnownTypeRegistry));

        JsonSerializer.Serialize(writer, model, flowchartSerializerOptions);
    }

    private ICollection<Connection> GetNotFoundConnections(JsonElement rootElement, IDictionary<string, IStep> steps, IEnumerable<Connection> connections, JsonSerializerOptions connectionSerializerOptions)
    {
        var customPropertiesElement = rootElement.TryGetProperty("customProperties", out var customPropertiesEl) ? customPropertiesEl : default;

        var notFoundConnectionsElement =
            customPropertiesElement.ValueKind != JsonValueKind.Undefined
                ? customPropertiesElement.TryGetProperty(NotFoundConnectionsKey, out var notFoundConnectionsEl)
                    ? notFoundConnectionsEl
                    : default
                : default;
        var notFoundConnections = notFoundConnectionsElement.ValueKind != JsonValueKind.Undefined ? DeserializeConnections(notFoundConnectionsElement, steps, connectionSerializerOptions) : new List<Connection>();

        // Add connections of NotFoundStep to the list if they aren't already in it.
        var notFoundSteps = steps.Values.Where(x => x is NotFoundStep).Cast<NotFoundStep>().ToList();
        var notFoundStepConnections = connections.Where(x => notFoundSteps.Contains(x.Source.Step)).ToList();

        foreach (var notFoundConnection in notFoundStepConnections)
        {
            if (notFoundConnections.All(x => x.Source != notFoundConnection.Source || x.Target != notFoundConnection.Target))
                notFoundConnections.Add(notFoundConnection);
        }

        return notFoundConnections;
    }

    private List<Connection> FindConnectionsThatCanBeRestored(IEnumerable<Connection> notFoundConnections, IEnumerable<IStep> steps)
    {
        var connectionsThatCanBeRestored = new List<Connection>();
        var foundSteps = steps.Where(x => x is not NotFoundStep).ToList();

        foreach (var notFoundConnection in notFoundConnections.ToList())
        {
            var missingSource = notFoundConnection.Source;
            var missingTarget = notFoundConnection.Target;

            // ReSharper disable ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
            // Step might be null in case of JSON missing information.
            var source = foundSteps.FirstOrDefault(x => x.Id == missingSource.Step?.Id);
            var target = foundSteps.FirstOrDefault(x => x.Id == missingTarget.Step?.Id);
            // ReSharper restore ConditionalAccessQualifierIsNonNullableAccordingToAPIContract

            if (source == null || target == null)
                continue;

            var connection = new Connection(new Endpoint(source, missingSource.Port), new Endpoint(target, missingTarget.Port));
            connectionsThatCanBeRestored.Add(connection);
        }

        return connectionsThatCanBeRestored;
    }

    private ICollection<Connection> DeserializeConnections(JsonElement connectionsElement, IDictionary<string, IStep> stepDictionary, JsonSerializerOptions options)
    {
        // 1) Nothing → empty
        if (connectionsElement.ValueKind == JsonValueKind.Undefined || connectionsElement.ValueKind == JsonValueKind.Null)
            return new List<Connection>();

        // 2) OData‐style wrapper: { "$values": [ … ] }
        if (connectionsElement.ValueKind == JsonValueKind.Object && connectionsElement.TryGetProperty("$values", out var valuesEl) && valuesEl.ValueKind == JsonValueKind.Array)
        {
            connectionsElement = valuesEl;
        }
        // 3) Single‐object (old style): wrap into a 1‑element array if it has a "source" property
        else if (connectionsElement.ValueKind == JsonValueKind.Object && connectionsElement.TryGetProperty("source", out _))
        {
            using var tmp = JsonDocument.Parse($"[{connectionsElement.GetRawText()}]");
            connectionsElement = tmp.RootElement;
        }

        // 4) If it’s still not an array, bail
        if (connectionsElement.ValueKind != JsonValueKind.Array)
            return new List<Connection>();

        // Shortcut: detect the classic flat‐connection JSON and parse manually
        var arr = connectionsElement.EnumerateArray().ToArray();
        if (arr.Length > 0 && arr[0].TryGetProperty("source", out var srcProp) && srcProp.ValueKind == JsonValueKind.String && arr[0].TryGetProperty("target", out var tgtProp) && tgtProp.ValueKind == JsonValueKind.String)
        {
            var list = new List<Connection>();

            foreach (var el in arr)
            {
                var srcId = el.GetProperty("source").GetString()!;
                var tgtId = el.GetProperty("target").GetString()!;
                var srcPort = el.TryGetProperty("sourcePort", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null;
                var tgtPort = el.TryGetProperty("targetPort", out var tp) && tp.ValueKind == JsonValueKind.String ? tp.GetString() : null;

                var srcAct = stepDictionary[srcId];
                var tgtAct = stepDictionary[tgtId];
                list.Add(new(new Endpoint(srcAct, srcPort), new Endpoint(tgtAct, tgtPort)));
            }

            return list;
        }

        // Otherwise, it's an array of nested‐object connections → delegate to your converters
        var serializer = new JsonSerializerOptions(options);

        // Legacy check: look for "sourcePort" on the first item to choose the old converter
        if (arr.Length > 0 && arr[0].TryGetProperty("sourcePort", out _))
            serializer.Converters.Add(new ObsoleteConnectionJsonConverter(stepDictionary));
        else
            serializer.Converters.Add(new ConnectionJsonConverter(stepDictionary, loggerFactory));

        var raw = connectionsElement.Deserialize<ICollection<Connection?>>(serializer) ?? new List<Connection?>();

        // drop any half‑baked entries
        return raw.Where(c => c != null && c.Source.Step != null! && c.Target.Step != null!).Cast<Connection>().ToList();
    }
}