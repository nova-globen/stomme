using Stomme.Engine.Extensions;
using static Stomme.Engine.Pipelines.Tests.Extensions.StepExecutionContextExtensions.TestHelpers;

namespace Stomme.Engine.Pipelines.Tests.Extensions.StepExecutionContextExtensions;

public class TriggerTests
{
    [Theory]
    [InlineData(true, null)] // null means use context.Step.Id
    [InlineData(false, "different-id")]
    public async Task IsTriggerOfPipeline_ReturnsExpectedResult(bool expectedResult, string? triggerStepId)
    {
        // Arrange
        var context = await CreateContextAsync();
        context.PipelineExecutionContext.TriggerStepId = triggerStepId ?? context.Step.Id;

        // Act
        var result = context.IsTriggerOfPipeline();

        // Assert
        Assert.Equal(expectedResult, result);
    }
}
