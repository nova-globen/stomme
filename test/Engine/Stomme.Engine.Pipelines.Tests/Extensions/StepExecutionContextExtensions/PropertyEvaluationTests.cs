using Stomme.Engine.Extensions;
using static Stomme.Engine.Pipelines.Tests.Extensions.StepExecutionContextExtensions.TestHelpers;

namespace Stomme.Engine.Pipelines.Tests.Extensions.StepExecutionContextExtensions;

public class PropertyEvaluationTests
{
    [Theory]
    [InlineData(false)] // Initial state
    [InlineData(true)]  // After setting flag
    public async Task HasEvaluatedProperties_TracksState(bool shouldSetFlag)
    {
        // Arrange
        var context = await CreateContextAsync();
        if (shouldSetFlag)
            context.SetHasEvaluatedProperties();

        // Act
        var result = context.GetHasEvaluatedProperties();

        // Assert
        Assert.Equal(shouldSetFlag, result);
    }
}
