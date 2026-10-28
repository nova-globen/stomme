using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Steps;
using static Stomme.Engine.Pipelines.Tests.Extensions.StepExecutionContextExtensions.TestHelpers;

namespace Stomme.Engine.Pipelines.Tests.Extensions.StepExecutionContextExtensions;

public class HierarchyNavigationTests
{
    [Theory]
    [InlineData("GetAncestors")]
    [InlineData("GetDescendants")]
    public async Task HierarchyNavigation_ReturnsEmpty_WhenNoRelatives(string methodName)
    {
        // Arrange
        var context = await CreateContextAsync();

        // Act
        var result = methodName switch
        {
            "GetAncestors" => context.GetAncestors().ToList(),
            "GetDescendants" => context.GetDescendants().ToList(),
            _ => throw new ArgumentException($"Unknown method: {methodName}")
        };

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task FindParent_ReturnsNull_WhenNoMatch()
    {
        // Arrange
        var context = await CreateContextAsync();

        // Act
        var parent = context.FindParent(x => x.Step is Sequence);

        // Assert
        Assert.Null(parent);
    }
}
