using System.Text.Json;
using System.Text.Json.Serialization;
using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Serialization.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Serialization.Converters;

/// <summary>
/// (De)serializes objects of type <see cref="IStep"/>.
/// </summary>
public class StepJsonConverter(
    IStepRegistry stepRegistry,
    IExpressionDescriptorRegistry expressionDescriptorRegistry,
    StepWriter stepWriter,
    IServiceProvider serviceProvider)
    : JsonConverter<IStep>
{
    /// <inheritdoc />
    public override IStep Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (!JsonDocument.TryParseValue(ref reader, out var doc))
            throw new JsonException("Failed to parse JsonDocument");

        var stepRoot = doc.RootElement;
        var stepTypeName = GetStepDetails(stepRoot, out var stepTypeVersion, out var stepDescriptor);
        var notFoundStepTypeName = StepTypeNameHelper.GenerateTypeName<NotFoundStep>();

        // If the step type is a NotFoundStep, try to extract the original step type name and version.
        if (stepTypeName.Equals(notFoundStepTypeName) && stepRoot.TryGetProperty("originalStepJson", out var originalStepJson))
        {
            stepRoot = JsonDocument.Parse(originalStepJson.GetString()!).RootElement;
            stepTypeName = GetStepDetails(stepRoot, out stepTypeVersion, out stepDescriptor);
        }

        var clonedOptions = GetClonedOptions(options);
        // If the step type is not found, create a NotFoundStep instead.
        if (stepDescriptor == null)
        {
            var notFoundStepDescriptor = stepRegistry.Find<NotFoundStep>()!;
            var notFoundStepResult = JsonStepConstructorContextHelper.CreateStep<NotFoundStep>(notFoundStepDescriptor, stepRoot, clonedOptions);
            LogExceptionsIfAny(notFoundStepResult);

            var notFoundStep = notFoundStepResult.Step;
            notFoundStep.Type = notFoundStepTypeName;
            notFoundStep.Version = 1;
            notFoundStep.MissingTypeName = stepTypeName;
            notFoundStep.MissingTypeVersion = stepTypeVersion;
            notFoundStep.OriginalStepJson = stepRoot.ToString();

            // Extract metadata from doc.RootElement rather than stepRoot.
            // In round-trip scenarios, stepRoot may have been reassigned to the inner originalStepJson (see line 37),
            // but we want the metadata from the current step being deserialized, which represents the NotFoundStep
            // placeholder's position and annotations in the designer.
            if (doc.RootElement.TryGetProperty("metadata", out var outerMetadataElement))
            {
                var outerMetadata = JsonSerializer.Deserialize<IDictionary<string, object>>(outerMetadataElement.GetRawText(), clonedOptions);
                if (outerMetadata != null)
                {
                    notFoundStep.Metadata = outerMetadata;
                }
            }

            // Set display text and description after metadata assignment to ensure they always reflect the current state
            notFoundStep.SetDisplayText($"Not Found: {stepTypeName}");
            notFoundStep.SetDescription($"Could not find step type {stepTypeName} with version {stepTypeVersion}");

            return notFoundStep;
        }

        var context = JsonStepConstructorContextHelper.Create(stepDescriptor, stepRoot, clonedOptions);
        var stepResult = stepDescriptor.Constructor(context);
        LogExceptionsIfAny(stepResult);

        return stepResult.Step;
    }

    void LogExceptionsIfAny(StepConstructionResult result)
    {
        if (!result.HasExceptions)
            return;

        var logger = serviceProvider.GetRequiredService<ILogger<StepJsonConverter>>();
        foreach (var exception in result.Exceptions)
            logger.LogWarning("An exception was thrown while constructing step with id '{stepId}': {Message}", result.Step.Id, exception.Message);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, IStep value, JsonSerializerOptions options)
    {
        var clonedOptions = GetClonedWriterOptions(options);
        var stepDescriptor = stepRegistry.Find(value.Type, value.Version);

        // Give the step descriptor a chance to customize the serializer options.
        clonedOptions = stepDescriptor?.ConfigureSerializerOptions?.Invoke(clonedOptions) ?? clonedOptions;

        stepWriter.WriteStep(writer, value, clonedOptions);
    }

    private string GetStepDetails(JsonElement stepRoot, out int stepTypeVersion, out StepDescriptor? stepDescriptor)
    {
        if (!stepRoot.TryGetProperty("type", out var stepTypeNameElement))
            throw new JsonException("Failed to extract step type property");

        var stepTypeName = stepTypeNameElement.GetString()!;
        stepDescriptor = null;
        stepTypeVersion = 0;

        // First, we check whether the step type name is a 'well-known' step; not a pipeline-as-step

        // If the step type version is specified, use that to find the step descriptor.
        if (stepRoot.TryGetProperty("version", out var stepVersionElement))
        {
            stepTypeVersion = stepVersionElement.GetInt32();
            stepDescriptor = stepRegistry.Find(stepTypeName, stepTypeVersion);
        }

        // If a version is not specified, or step with specified version is not found: use the latest version of the step descriptor.
        if (stepDescriptor == null)
        {
            stepDescriptor = stepRegistry.Find(stepTypeName);
            stepTypeVersion = stepDescriptor?.Version ?? 0;
        }

        // This is a special case when working with the PipelineDefinitionStep: pipelineDefinitionVersionId should be used to find the pipeline-as-step.
        if (stepRoot.TryGetProperty("pipelineDefinitionVersionId", out var pipelineDefinitionVersionIdElement))
        {
            var stepDescriptorOverride = FindStepDescriptorByCustomProperty("PipelineDefinitionVersionId", pipelineDefinitionVersionIdElement);
            if (stepDescriptorOverride is null)
                return stepTypeName;

            stepDescriptor = stepDescriptorOverride;
            stepTypeVersion = stepDescriptor.Version;
        }
        // This is also a special case when working with the PipelineDefinitionStep: if no 'well-known' step could be found, it might be a pipeline-as-step with a pipelineDefinitionId
        else if (stepDescriptor is null
                 && stepRoot.TryGetProperty("pipelineDefinitionId", out var pipelineDefinitionIdElement)
                 && pipelineDefinitionIdElement.ValueKind == JsonValueKind.String)
        {
            stepDescriptor = FindStepDescriptorByCustomProperty("PipelineDefinitionId", pipelineDefinitionIdElement);
            stepTypeVersion = stepDescriptor?.Version ?? 0;
        }

        return stepTypeName;
    }

    private StepDescriptor? FindStepDescriptorByCustomProperty(string customPropertyName, JsonElement valueElement)
    {
        if (valueElement.ValueKind != JsonValueKind.String)
            return null;

        var searchValue = valueElement.GetString();
        return stepRegistry.Find(x => x.CustomProperties.TryGetValue(customPropertyName, out var value) && (string?)value == searchValue);
    }

    private JsonSerializerOptions GetClonedOptions(JsonSerializerOptions options)
    {
        var clonedOptions = new JsonSerializerOptions(options);
        clonedOptions.Converters.Add(new InputJsonConverterFactory(serviceProvider));
        clonedOptions.Converters.Add(new OutputJsonConverterFactory(serviceProvider));
        clonedOptions.Converters.Add(new ExpressionJsonConverterFactory(expressionDescriptorRegistry));
        return clonedOptions;
    }

    private JsonSerializerOptions GetClonedWriterOptions(JsonSerializerOptions options)
    {
        var clonedOptions = GetClonedOptions(options);
        clonedOptions.Converters.Add(new JsonIgnoreCompositeRootConverterFactory(serviceProvider.GetRequiredService<StepWriter>()));
        return clonedOptions;
    }
}