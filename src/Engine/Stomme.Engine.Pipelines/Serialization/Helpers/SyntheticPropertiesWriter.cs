using System.Text.Json;
using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Pipelines.Models;
using Humanizer;

namespace Stomme.Engine.Pipelines.Serialization.Helpers;

/// <summary>
/// Helper class for writing synthetic properties of a step to a JSON writer.
/// </summary>
public class SyntheticPropertiesWriter(IExpressionDescriptorRegistry expressionDescriptorRegistry)
{
    /// <summary>
    /// Writes synthetic properties of a step to a JSON writer.
    /// </summary>
    public void WriteSyntheticProperties(Utf8JsonWriter writer, IStep value, StepDescriptor stepDescriptor, JsonSerializerOptions options)
    {
        WriteSyntheticInputProperties(writer, value, stepDescriptor, options);
        WriteSyntheticOutputProperties(writer, value, stepDescriptor, options);
    }

    private void WriteSyntheticInputProperties(Utf8JsonWriter writer, IStep value, StepDescriptor stepDescriptor, JsonSerializerOptions options)
    {
        var syntheticInputs = stepDescriptor.Inputs.Where(x => x.IsSynthetic).ToList();

        foreach (var inputDescriptor in syntheticInputs)
        {
            var inputName = inputDescriptor.Name;
            var propertyName = inputName.Camelize();

            if (!value.SyntheticProperties.TryGetValue(inputName, out var inputValue))
                continue;

            writer.WritePropertyName(propertyName);

            var input = (Input?)inputValue;

            if (input == null)
            {
                writer.WriteNullValue();
                continue;
            }

            var expression = input.Expression;
            var expressionType = expression?.Type;
            var inputType = input.Type;
            var memoryReferenceId = input.MemoryBlockReference().Id;
            var expressionDescriptor = expressionType != null ? expressionDescriptorRegistry.Find(expressionType) : null;

            if (expressionDescriptor == null)
                throw new Exception($"Syntax descriptor with expression type {expressionType} not found in registry");

            var inputModel = new
            {
                TypeName = inputType,
                Expression = expression,
                MemoryReference = new
                {
                    Id = memoryReferenceId
                }
            };

            JsonSerializer.Serialize(writer, inputModel, inputModel.GetType(), options);
        }
    }

    private void WriteSyntheticOutputProperties(Utf8JsonWriter writer, IStep value, StepDescriptor stepDescriptor, JsonSerializerOptions options)
    {
        var syntheticOutputs = stepDescriptor.Outputs.Where(x => x.IsSynthetic).ToList();

        // Write synthetic outputs. 
        foreach (var outputDescriptor in syntheticOutputs)
        {
            var outputName = outputDescriptor.Name;
            var propertyName = outputName.Camelize();

            if (!value.SyntheticProperties.TryGetValue(outputName, out var outputValue))
                continue;

            writer.WritePropertyName(propertyName);
            var output = (Output?)outputValue;

            if (output == null)
            {
                writer.WriteNullValue();
                continue;
            }

            var outputType = outputDescriptor.Type;
            var memoryReferenceId = output.MemoryBlockReference().Id;

            var outputModel = new
            {
                TypeName = outputType,
                MemoryReference = new
                {
                    Id = memoryReferenceId
                }
            };

            JsonSerializer.Serialize(writer, outputModel, outputModel.GetType(), options);
        }
    }
}