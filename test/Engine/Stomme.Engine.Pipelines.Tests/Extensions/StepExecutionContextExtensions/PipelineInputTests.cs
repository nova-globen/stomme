using Stomme.Engine.Extensions;
using static Stomme.Engine.Pipelines.Tests.Extensions.StepExecutionContextExtensions.TestHelpers;

namespace Stomme.Engine.Pipelines.Tests.Extensions.StepExecutionContextExtensions;

public class PipelineInputTests
{
    [Theory]
    [InlineData("TestKey", "TestValue", true, "TestValue", null)] // Key exists, string value
    [InlineData("NonExistentKey", null, false, null, null)] // Key doesn't exist
    [InlineData("NumberKey", null, true, "42", 42)] // Type conversion from int to string
    public async Task TryGetPipelineInput_HandlesVariousScenarios(
        string key,
        string? stringValue,
        bool expectedResult,
        string? expectedValue,
        object? inputValue)
    {
        // Arrange
        var context = await CreateContextAsync();
        
        if (stringValue != null)
            context.PipelineExecutionContext.Input[key] = stringValue;
        else if (inputValue != null)
            context.PipelineExecutionContext.Input[key] = inputValue;

        // Act
        var result = context.TryGetPipelineInput<string>(key, out var value);

        // Assert
        Assert.Equal(expectedResult, result);
        Assert.Equal(expectedValue, value);
    }

    [Fact]
    public async Task GetPipelineInput_ReturnsValue_WhenKeyExists()
    {
        // Arrange
        var context = await CreateContextAsync();
        context.PipelineExecutionContext.Input["TestKey"] = "TestValue";

        // Act
        var value = context.GetPipelineInput<string>("TestKey");

        // Assert
        Assert.Equal("TestValue", value);
    }

    [Fact]
    public async Task GetPipelineInput_UsesTypeName_WhenKeyNotProvided()
    {
        // Arrange
        var context = await CreateContextAsync();
        context.PipelineExecutionContext.Input["String"] = "TestValue";

        // Act
        var value = context.GetPipelineInput<string>();

        // Assert
        Assert.Equal("TestValue", value);
    }

    [Fact]
    public async Task GetPipelineInput_ThrowsException_WhenKeyDoesNotExist()
    {
        // Arrange
        var context = await CreateContextAsync();

        // Act & Assert
        Assert.Throws<KeyNotFoundException>(() => context.GetPipelineInput<string>("NonExistentKey"));
    }
}
