using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Serialization.Helpers;

/// <summary>
/// The StepWriter class is responsible for writing a step to a JSON writer using the provided options.
/// </summary>
public class StepWriter(IStepRegistry stepRegistry, SyntheticPropertiesWriter syntheticPropertiesWriter, ILogger<StepWriter> logger)
{
    /// <summary>
    /// Writes a step to a JSON writer using the provided options.
    /// </summary>
    /// <param name="writer">The JSON writer.</param>
    /// <param name="value">The step to write.</param>
    /// <param name="options">The JSON serialization options.</param>
    /// <param name="ignoreSpecializedConverters">Controls whether to ignore the availability of converters that can handle IStep objects.</param>
    /// <param name="excludeChildren">A flag indicating whether to exclude child steps.</param>
    /// <param name="propertyFilter">An additional property filter. Returning true will skip the property.</param>
    public void WriteStep(Utf8JsonWriter writer, IStep? value, JsonSerializerOptions options, bool ignoreSpecializedConverters = false, bool excludeChildren = false, Func<PropertyInfo, bool>? propertyFilter = null)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        if (!ignoreSpecializedConverters)
        {
            // Check if there's a specialized converter for the step.
            var valueType = value.GetType();
            var specializedConverter = options.Converters.FirstOrDefault(x => x.CanConvert(valueType));
            if (specializedConverter != null)
            {
                JsonSerializer.Serialize(writer, value, valueType, options);
                return;
            }
        }

        writer.WriteStartObject();

        var properties = value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var property in properties)
        {
            if (property.GetCustomAttribute<JsonIgnoreAttribute>() != null)
                continue;

            if (excludeChildren)
            {
                if (typeof(IStep).IsAssignableFrom(property.PropertyType))
                    continue;

                if (typeof(IEnumerable<IStep>).IsAssignableFrom(property.PropertyType))
                    continue;
            }

            if (propertyFilter?.Invoke(property) == true)
                continue;

            var propName = options.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name;
            writer.WritePropertyName(propName);
            var input = property.GetValue(value);

            if (input == null)
            {
                writer.WriteNullValue();
                continue;
            }

            if (property.Name == nameof(IStep.CustomProperties))
            {
                var customProperties = new Dictionary<string, object>(value.CustomProperties);
                foreach (var kvp in customProperties)
                {
                    if (kvp.Value is IStep or IEnumerable<IStep>)
                        customProperties.Remove(kvp.Key);
                }

                input = customProperties;
            }

            JsonSerializer.Serialize(writer, input, options);
        }

        var stepDescriptor = stepRegistry.Find(value.Type, value.Version);

        if (stepDescriptor == null)
            logger.LogDebug("No descriptor found for step {StepType}", value.GetType().Name);
        else
            syntheticPropertiesWriter.WriteSyntheticProperties(writer, value, stepDescriptor, options);

        writer.WriteEndObject();
    }
}