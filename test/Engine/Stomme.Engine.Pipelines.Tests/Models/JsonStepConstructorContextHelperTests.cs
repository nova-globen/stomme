
using System.Text.Json;
using System.Text.Json.Serialization;
using Stomme.Engine.Pipelines.Exceptions;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Tests.Models;

public sealed class JsonStepConstructorContextHelperTests
{
    private static readonly JsonSerializerOptions _serializerOptions = new();

    [Theory]
    [InlineData("_Metadata")]
    [InlineData("Metadata")]
    [InlineData("_CustomProperties")]
    [InlineData("CustomProperties")]
    public void When_InputName_Is_ReservedKeyWord_Then_AddsException_WhenReadingInputs(string reservedInputName)
    {
        // Arrange
        var stepDescriptor = new StepDescriptor
        {
            Inputs =
            [
                new() 
                { 
                    Name = reservedInputName, 
                    Type = typeof(object),
                    IsSynthetic = true
                }
            ]
        };
        var jsonElement = GetJsonElementWithReservedInputName(reservedInputName);

        // Act
        var result = JsonStepConstructorContextHelper.CreateStep<PipelineAsStep>(
            stepDescriptor,
            jsonElement,
            _serializerOptions
        );

        // Assert
        Assert.Single(result.Exceptions, e => e is InvalidStepDescriptorInputException);
    }

    private static JsonElement GetJsonElementWithReservedInputName(string inputName)
    {
        const string json = "{\"pipelineDefinitionId\":\"Id\",\"id\":\"Id\",\"nodeId\":\"NodeId\",\"name\":\"Pipeline-as-step\",\"type\":\"PipelineAsStep\",\"customProperties\":{\"canStartPipeline\":false,\"runAsynchronously\":false},\"metadata\":{\"designer\":{\"position\":{\"x\":1525.99609375,\"y\":171.5625},\"size\":{\"width\":230.4296875,\"height\":68.4375}},\"displayText\":\"Test\"},\"<inputName>\":{\"typeName\":\"Object\",\"expression\":{\"type\":\"JavaScript\",\"value\":\"return {\\n    \\u0022key\\u0022 : obj.value\\n}\"},\"memoryReference\":{\"id\":\"memref:input--name\"}}}";
        var jsonWithInputName = json.Replace("<inputName>", inputName);
        return JsonElement.Parse(jsonWithInputName);
    }

    private class PipelineAsStep : IStep
    {
        public string Id { get; set; } = "Id";
        public string NodeId { get; set; } = "NodeId";
        public string? Name { get; set; } = "Pipeline-as-step";
        public string Type { get; set; } = "PipelineAsStep";
        public int Version { get; set; } = 1;

        [JsonIgnore]
        public IDictionary<string, object> CustomProperties { get; set; } = new Dictionary<string, object>();
        public IDictionary<string, object> SyntheticProperties { get; set; } = new Dictionary<string, object>();

        [JsonIgnore]
        public IDictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public ValueTask<bool> CanExecuteAsync(StepExecutionContext context)
        {
            return new(true);
        }

        public ValueTask ExecuteAsync(StepExecutionContext context)
        {
            return new();
        }
    }
}
