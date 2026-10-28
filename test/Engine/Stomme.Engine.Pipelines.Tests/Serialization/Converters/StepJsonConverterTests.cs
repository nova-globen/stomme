using System.Text.Json;
using Stomme.Engine.Common.Serialization;
using Stomme.Engine.Expressions.Services;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Management.Steps.PipelineDefinitionStep;
using Stomme.Engine.Pipelines.Management.Services;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Serialization.Configurators;
using Stomme.Engine.Pipelines.Serialization.Converters;
using Stomme.Engine.Pipelines.Serialization.Helpers;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Stomme.Engine.Pipelines.Tests.Serialization.Converters;

public sealed class StepJsonConverterTests
{
    [Fact]
    public void When_DeserializeKnownStep_And_TypeNameSpecified_Then_FindsAndInstantiatesStep()
    {
        // Arrange
        var stepRegistry = CreateStepRegistry(WriteLineStepTypeName, WriteLineStep);
        var sut = CreateSut(stepRegistry);

        // Act
        var result = Execute(sut, WriteLineStepJson_WithoutVersion);

        // Assert
        Assert.Same(WriteLineStep, result);
    }

    [Fact]
    public void When_DeserializeKnownStep_And_TypeNameSpecified_And_VersionSpecified_Then_FindsAndInstantiatesStep()
    {
        // Arrange
        var stepRegistry = CreateStepRegistry(WriteLineStepTypeName, WriteLineStep, version: 1);
        var sut = CreateSut(stepRegistry);

        // Act
        var result = Execute(sut, WriteLineStepJson_WithVersion);

        // Assert
        Assert.Same(WriteLineStep, result);
    }

    [Fact]
    public void When_DeserializeUnknownStep_Then_ReturnsNotFoundStep()
    {
        // Arrange
        var stepRegistry = Substitute.For<IStepRegistry>();
        stepRegistry
            .Find(NotFoundStepTypeName)
            .Returns(new StepDescriptor());

        var sut = CreateSut(stepRegistry);

        // Act
        var result = Execute(sut, UnknownStepJson);

        // Assert
        Assert.IsType<NotFoundStep>(result);
        var notFoundStep = (NotFoundStep)result;
        Assert.Equal(UnknownStepTypeName, notFoundStep.MissingTypeName);
        Assert.Equal(0, notFoundStep.MissingTypeVersion);

        var expectedJsonDoc = JsonDocument.Parse(UnknownStepJson);
        var actualJsonDoc = JsonDocument.Parse(notFoundStep.OriginalStepJson);
        Assert.Equal(expectedJsonDoc.RootElement.ToString(), actualJsonDoc.RootElement.ToString());
        Assert.True(notFoundStep.Metadata.ContainsKey("displayText"));
        Assert.True(notFoundStep.Metadata.ContainsKey("description"));
    }

    [Fact]
    public void When_DeserializePipelineAsStep_And_PipelineDefinitionIdSpecified_Then_FindsAndInstantiatesStep()
    {
        // Arrange
        var stepRegistry = CreateStepRegistry_FindByCustomProperty(
            PipelineDefinitionIdCustomPropertyName,
            PipelineAsStepDefinitionId
        );

        var sut = CreateSut(stepRegistry);

        // Act
        var result = Execute(sut, PipelineAsStepJson_WithDefinitionId);

        // Assert
        Assert.Same(PipelineAsStep, result);
    }

    [Fact]
    public void When_DeserializePipelineAsStep_And_PipelineDefinitionVersionIdSpecified_Then_FindsAndInstantiatesStep()
    {
        // Arrange
        var stepRegistry = CreateStepRegistry_FindByCustomProperty(
            PipelineDefinitionVersionIdCustomPropertyName,
            PipelineAsStepDefinitionVersionId
        );
        var sut = CreateSut(stepRegistry);

        // Act
        var result = Execute(sut, PipelineAsStepJson_WithVersionId);

        // Assert
        Assert.Same(PipelineAsStep, result);
    }

    [Fact]
    public void When_DeserializePipelineAsStep_And_TypeNameSpecified_Then_FindsAndInstantiatesStep()
    {
        // Arrange
        var stepRegistry = CreateStepRegistry(PipelineAsStepTypeName, PipelineAsStep);
        var sut = CreateSut(stepRegistry);

        // Act
        var result = Execute(sut, PipelineAsStepJson_WithTypeNameOnly);

        // Assert
        Assert.Same(PipelineAsStep, result);
    }

    static IStep? Execute(StepJsonConverter sut, string json) =>
        JsonSerializer.Deserialize<IStep>(json, GetSerializerOptions(sut));

    static IStepRegistry CreateStepRegistry(string typeName, IStep step, int? version = null)
    {
        var descriptor = new StepDescriptor { Constructor = _ => new(step) };
        var stepRegistry = Substitute.For<IStepRegistry>();

        if (version.HasValue)
            stepRegistry.Find(typeName, version.Value).Returns(descriptor);
        else
            stepRegistry.Find(typeName).Returns(descriptor);

        return stepRegistry;
    }

    static IStepRegistry CreateStepRegistry_FindByCustomProperty(string customPropertyName, string customPropertyValue)
    {
        var descriptor = new StepDescriptor
        {
            Constructor = _ => new(PipelineAsStep),
            CustomProperties = { [customPropertyName] = customPropertyValue }
        };

        var stepRegistry = Substitute.For<IStepRegistry>();

        // Mock type name lookups to return null, ensuring the custom property lookup path is tested
        stepRegistry.Find(Arg.Any<string>()).Returns((StepDescriptor?)null);
        stepRegistry.Find(Arg.Any<string>(), Arg.Any<int>()).Returns((StepDescriptor?)null);

        stepRegistry
            .Find(Arg.Any<Func<StepDescriptor, bool>>())
            .Returns(callInfo => callInfo.Arg<Func<StepDescriptor, bool>>()(descriptor) ? descriptor : null);

        return stepRegistry;
    }

    static JsonSerializerOptions GetSerializerOptions(StepJsonConverter sut) => new()
    {
        Converters =
        {
            sut,
            new TypeJsonConverter(WellKnownTypeRegistry.CreateDefault()),
            new PolymorphicObjectConverterFactory()
        },
        TypeInfoResolver = new ModifiableJsonTypeInfoResolver(new CustomConstructorConfigurator().GetModifiers())
    };

    private const string UnknownStepTypeName = "Some.Unknown.Step";
    private const string PipelineAsStepTypeName = "Test-SubPipeline";
    private const string PipelineDefinitionIdCustomPropertyName = "PipelineDefinitionId";
    private const string PipelineDefinitionVersionIdCustomPropertyName = "PipelineDefinitionVersionId";
    private const string PipelineAsStepDefinitionId = "bd94124913202141";
    private const string PipelineAsStepDefinitionVersionId = "aaa1112fff2443";
    private static readonly WriteLine WriteLineStep = new("Hello world!");
    private static readonly string WriteLineStepTypeName = StepTypeNameHelper.GenerateTypeName<WriteLine>();
    private static readonly string NotFoundStepTypeName = StepTypeNameHelper.GenerateTypeName<NotFoundStep>();

    private const string WriteLineStepJson_WithVersion = 
"""
 {
     "text": {
         "typeName": "String",
         "expression": {
             "type": "Literal",
             "value": "Hello world!"
         }
     },
     "id": "b54b024aa7469287",
     "nodeId": "Pipeline1:51139b0cc83d3b07:b54b024aa7469287",
     "name": "WriteLine1",
     "type": "Stomme.WriteLine",
     "version": 1,
     "customProperties": {
         "canStartPipeline": false,
         "runAsynchronously": false
     },
     "metadata": {}
 }
 """;

    private const string UnknownStepJson = 
"""
{
   "text": {
       "typeName": "String",
       "expression": {
           "type": "Literal",
           "value": "Hello world!"
       }
   },
   "id": "b54b024aa7469287",
   "nodeId": "Pipeline1:51139b0cc83d3b07:b54b024aa7469287",
   "name": "Unknown1",
   "type": "Some.Unknown.Step",
   "customProperties": {
       "canStartPipeline": false,
       "runAsynchronously": false
   },
   "metadata": {}
}
""";

    private const string WriteLineStepJson_WithoutVersion = 
"""
{
    "text": {
        "typeName": "String",
        "expression": {
            "type": "Literal",
            "value": "Hello world!"
        }
    },
    "id": "b54b024aa7469287",
    "nodeId": "Pipeline1:51139b0cc83d3b07:b54b024aa7469287",
    "name": "WriteLine1",
    "type": "Stomme.WriteLine",
    "customProperties": {
        "canStartPipeline": false,
        "runAsynchronously": false
    },
    "metadata": {}
}
""";

    private const string PipelineAsStepJson_WithVersionId = 
"""
{
    "pipelineDefinitionVersionId": "aaa1112fff2443",
    "id": "5fc551ed366fe9fd",
    "nodeId": "Pipeline2:cad9d2186550a8d7:5fc551ed366fe9fd",
    "name": "Test-SubPipeline1",
    "type": "Test-SubPipeline",
    "customProperties": {
        "canStartPipeline": false,
        "runAsynchronously": false,
        "logPersistenceMode": {
            "default": "Inherit",
            "inputs": {},
            "outputs": {}
        },
        "logPersistenceConfig": {
            "default": null,
            "internalState": null,
            "inputs": {},
            "outputs": {}
        }
    },
    "metadata": {}
}
""";

    private const string PipelineAsStepJson_WithDefinitionId = 
"""
{
   "pipelineDefinitionId": "bd94124913202141",
   "id": "5fc551ed366fe9fd",
   "nodeId": "Pipeline2:cad9d2186550a8d7:5fc551ed366fe9fd",
   "name": "Test-SubPipeline1",
   "type": "Test-SubPipeline",
   "customProperties": {
       "canStartPipeline": false,
       "runAsynchronously": false,
       "logPersistenceMode": {
           "default": "Inherit",
           "inputs": {},
           "outputs": {}
       },
       "logPersistenceConfig": {
           "default": null,
           "internalState": null,
           "inputs": {},
           "outputs": {}
       }
   },
   "metadata": {}
}
""";

    private const string PipelineAsStepJson_WithTypeNameOnly = 
"""
{
   "id": "5fc551ed366fe9fd",
   "nodeId": "Pipeline2:cad9d2186550a8d7:5fc551ed366fe9fd",
   "name": "Test-SubPipeline1",
   "type": "Test-SubPipeline",
   "customProperties": {
       "canStartPipeline": false,
       "runAsynchronously": false,
       "logPersistenceMode": {
           "default": "Inherit",
           "inputs": {},
           "outputs": {}
       },
       "logPersistenceConfig": {
           "default": null,
           "internalState": null,
           "inputs": {},
           "outputs": {}
       }
   },
   "metadata": {}
}
""";

    private static readonly IStep PipelineAsStep = new PipelineDefinitionStep
    {
        PipelineDefinitionId = PipelineAsStepDefinitionId,
        PipelineDefinitionVersionId = PipelineAsStepDefinitionVersionId
    };

    private static StepJsonConverter CreateSut(IStepRegistry stepRegistry)
    {
        var expressionDescriptorRegistry = new ExpressionDescriptorRegistry([]);
        var serviceProvider = Substitute.For<IServiceProvider>();
        serviceProvider
            .GetService(typeof(ILogger<StepJsonConverter>))!
            .Returns(CreateLogger<StepJsonConverter>());

        return new(
            stepRegistry,
            expressionDescriptorRegistry,
            new(
                stepRegistry,
                new(expressionDescriptorRegistry),
                CreateLogger<StepWriter>()
            ),
            serviceProvider
        );
    }

    private static ILogger<T> CreateLogger<T>() => LoggerFactory.Create(_ => { }).CreateLogger<T>();
}