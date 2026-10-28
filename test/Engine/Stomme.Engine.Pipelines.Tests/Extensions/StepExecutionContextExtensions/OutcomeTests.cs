using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Attributes;
using static Stomme.Engine.Pipelines.Tests.Extensions.StepExecutionContextExtensions.TestHelpers;

namespace Stomme.Engine.Pipelines.Tests.Extensions.StepExecutionContextExtensions;

public class OutcomeTests
{
    [Theory]
    [InlineData(typeof(TestStepWithPort), "Done")]
    [InlineData(typeof(TestStepWithNamedPort), "CustomOutcome")]
    public async Task GetOutcomeName_ReturnsExpectedName(Type stepType, string expectedOutcomeName)
    {
        // Arrange
        var step = (Step)Activator.CreateInstance(stepType)!;
        var context = await CreateContextAsync(step);

        // Act
        var outcomeName = context.GetOutcomeName("Done");

        // Assert
        Assert.Equal(expectedOutcomeName, outcomeName);
    }

    private class TestStepWithPort : Step
    {
        public IStep? Done { get; set; }
    }

    private class TestStepWithNamedPort : Step
    {
        [Port("CustomOutcome")]
        public IStep? Done { get; set; }
    }
}
