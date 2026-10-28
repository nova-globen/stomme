using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Models;
using static Stomme.Engine.Pipelines.Tests.Extensions.StepExecutionContextExtensions.TestHelpers;

namespace Stomme.Engine.Pipelines.Tests.Extensions.StepExecutionContextExtensions;

public class ResultTests
{
    [Fact]
    public async Task SetResult_SetsResultProperty_WhenStepImplementsIStepWithResult()
    {
        // Arrange
        var step = new StepWithResult();
        var context = await CreateContextAsync(step);
        var testValue = "ResultValue";

        // Act
        context.SetResult(testValue);

        // Assert
        var result = context.Get(step.Result);
        Assert.Equal(testValue, result);
    }

    [Fact]
    public async Task SetResult_ThrowsException_WhenStepDoesNotImplementIStepWithResult()
    {
        // Arrange
        var context = await CreateContextAsync();

        // Act & Assert
        Assert.Throws<Exception>(() => context.SetResult("value"));
    }

    private class StepWithResult : Step, IStepWithResult
    {
        public Output? Result { get; set; } = new Output<object?>();
    }
}
