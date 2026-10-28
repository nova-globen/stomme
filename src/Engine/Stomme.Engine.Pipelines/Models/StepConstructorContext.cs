using System.Text.Json;
using System.Text.Json.Nodes;
using Stomme.Engine.Expressions.Helpers;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Memory;
using Humanizer;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Models;

public record StepConstructorContext(StepDescriptor StepDescriptor, Func<Type, StepConstructionResult> StepFactory)
{
    public StepConstructionResult<T> CreateStep<T>() where T : IStep => StepFactory(typeof(T)).Cast<T>();
    public StepConstructionResult CreateStep(Type type) => StepFactory(type);
}

public static class JsonStepConstructorContextHelper
{
    public static StepConstructorContext Create(StepDescriptor stepDescriptor, JsonElement element, JsonSerializerOptions serializerOptions)
    {
        return new StepConstructorContext(stepDescriptor, type => CreateStep(stepDescriptor, type, element, serializerOptions));
    }

    public static StepConstructionResult<T> CreateStep<T>(StepDescriptor stepDescriptor, JsonElement element, JsonSerializerOptions serializerOptions) where T : IStep
    {
        return CreateStep(stepDescriptor, typeof(T), element, serializerOptions).Cast<T>();
    }

    public static StepConstructionResult CreateStep(StepDescriptor stepDescriptor, Type type, JsonElement element, JsonSerializerOptions serializerOptions)
    {
        var exceptions = new List<Exception>();

        // 1) Grab the raw text
        var raw = element.GetRawText();

        // 2) Parse into a JsonNode so we can mutate
        var node = JsonNode.Parse(raw)!;

        // 3) Strip out any "_type" metadata wrappers
        StripTypeMetadata(node);

        // 4) Get a cleaned JSON string
        var cleanedJson = node.ToJsonString(new(JsonSerializerDefaults.Web));

        // 5) Parse it back to a JsonDocument so we can get a clean JsonElement
        using var doc = JsonDocument.Parse(cleanedJson);
        var cleanedElement = doc.RootElement.Clone();

        // 6) Deserialize into IStep, or create a plain instance
        var step = (IStep)JsonSerializer.Deserialize(cleanedJson, type, serializerOptions)!;

        // 7) Pull out your boolean flags from the cleaned element
        var canStartPipeline = GetBoolean(cleanedElement, "canStartPipeline");
        var runAsynchronously = GetNullableBoolean(cleanedElement, "runAsynchronously") ?? stepDescriptor.RunAsynchronously;

        // 8) If composite, setup
        if (step is IComposite composite)
            composite.Setup();

        // 9) Existing synthetic inputs/outputs routines, using the cleanedElement
        ReadSyntheticInputs(stepDescriptor, step, cleanedElement, serializerOptions, exceptions);
        ReadSyntheticOutputs(stepDescriptor, step, cleanedElement);

        // 10) Finally re‑apply those flags
        step.SetCanStartPipeline(canStartPipeline);
        step.SetRunAsynchronously(runAsynchronously);

        return new(step, exceptions);
    }

    /// <summary>
    /// Recursively remove any "_type" properties and unwrap any
    /// { "_type": "...", "items": [ ... ] } or "values": [ ... ] wrappers
    /// </summary>
    private static void StripTypeMetadata(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                // First recurse into each child
                foreach (var key in obj.Select(kvp => kvp.Key).ToList())
                {
                    var child = obj[key];
                    if (child is JsonObject wrapper && wrapper.ContainsKey("_type"))
                    {
                        // If it has an "items" array, replace the whole property with that array
                        if (wrapper["items"] is JsonArray items)
                        {
                            obj[key] = items;
                            StripTypeMetadata(items);
                            continue;
                        }

                        // Or a "values" array
                        if (wrapper["values"] is JsonArray values)
                        {
                            obj[key] = values;
                            StripTypeMetadata(values);
                            continue;
                        }
                    }

                    // Otherwise just recurse normally
                    if (child != null)
                        StripTypeMetadata(child);
                }

                // And remove any stray _type on this object
                obj.Remove("_type");
                break;

            case JsonArray arr:
                for (var i = 0; i < arr.Count; i++)
                {
                    var element = arr[i];
                    if (element is JsonObject w && w.ContainsKey("_type"))
                    {
                        if (w["items"] is JsonArray items)
                        {
                            arr[i] = items;
                            StripTypeMetadata(items);
                            continue;
                        }

                        if (w["values"] is JsonArray values)
                        {
                            arr[i] = values;
                            StripTypeMetadata(values);
                            continue;
                        }
                    }

                    if (element != null)
                        StripTypeMetadata(element);
                }

                break;

            // primitives—nothing to do
            default:
                break;
        }
    }

    private static void ReadSyntheticInputs(StepDescriptor stepDescriptor, IStep step, JsonElement stepRoot, JsonSerializerOptions options, List<Exception> exceptions)
    {
        foreach (var inputDescriptor in stepDescriptor.Inputs.Where(x => x.IsSynthetic))
        {
            var inputName = inputDescriptor.Name;
            var propertyName = inputName.Camelize();
            var nakedType = inputDescriptor.Type;
            var wrappedType = typeof(Input<>).MakeGenericType(nakedType);

            if (!stepRoot.TryGetProperty(propertyName, out var propertyElement) || propertyElement.ValueKind == JsonValueKind.Null || propertyElement.ValueKind == JsonValueKind.Undefined)
                continue;

            if(propertyElement.ValueKind == JsonValueKind.Object && !propertyElement.TryGetProperty("typeName", out var _))
            {
                var exception = new InvalidStepDescriptorInputException(
                    $"Step descriptor '{stepDescriptor.Name}' has invalid input property '{propertyName}'; missing required property 'typeName'"
                );
                exceptions.Add(exception);
                continue;
            }

            var isWrapped = propertyElement.ValueKind == JsonValueKind.Object && propertyElement.GetProperty("typeName").ValueKind != JsonValueKind.Undefined;

            if (isWrapped)
            {
                var json = propertyElement.ToString();
                var inputValue = JsonSerializer.Deserialize(json, wrappedType, options);

                step.SyntheticProperties[inputName] = inputValue!;
            }
            else
            {
                step.SyntheticProperties[inputName] = propertyElement.ConvertTo(inputDescriptor.Type)!;
            }
        }
    }

    private static void ReadSyntheticOutputs(StepDescriptor stepDescriptor, IStep step, JsonElement stepRoot)
    {
        foreach (var outputDescriptor in stepDescriptor.Outputs.Where(x => x.IsSynthetic))
        {
            var outputName = outputDescriptor.Name;
            var propertyName = outputName.Camelize();
            var nakedType = outputDescriptor.Type;
            var wrappedType = typeof(Output<>).MakeGenericType(nakedType);

            if (!stepRoot.TryGetProperty(propertyName, out var propertyElement) || propertyElement.ValueKind == JsonValueKind.Null || propertyElement.ValueKind == JsonValueKind.Undefined)
                continue;

            var memoryReferenceElement = propertyElement.GetProperty("memoryReference");

            if (!memoryReferenceElement.TryGetProperty("id", out var memoryReferenceIdElement))
                continue;

            var variable = new Variable
            {
                Id = memoryReferenceIdElement.GetString()!
            };
            variable.Name = variable.Id;

            var output = Activator.CreateInstance(wrappedType, variable)!;

            step.SyntheticProperties[outputName] = output!;
        }
    }

    private static bool GetBoolean(JsonElement element, string propertyName)
    {
        return GetNullableBoolean(element, propertyName) ?? false;
    }

    private static bool? GetNullableBoolean(JsonElement element, string propertyName)
    {
        var propertyNames = new[]
        {
            propertyName.Camelize(), propertyName.Pascalize()
        };

        foreach (var name in propertyNames)
        {
            if (element.TryGetProperty("customProperties", out var customPropertyElement))
            {
                if (customPropertyElement.TryGetProperty(name, out var canStartPipelineElement))
                    return (bool?)canStartPipelineElement.GetValue();
            }

            if (element.TryGetProperty(propertyName.Camelize(), out var property)
                && (bool?)property.GetValue() is { } propValue)
                return propValue;
        }

        return null;
    }
}