using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Attributes;
using Xunit;

namespace Stomme.Engine.Testing.Shared;

/// <summary>
/// General extension methods for StepTestFixture.
/// </summary>
public static class StepTestFixtureExtensions
{
    /// <summary>
    /// Validates that a step has the expected attribute configuration.
    /// This is useful for ensuring steps are properly decorated with metadata.
    /// </summary>
    /// <param name="fixture">The test fixture (unused but enables extension method syntax)</param>
    /// <param name="expectedNamespace">Expected namespace (e.g., "Stomme")</param>
    /// /// <param name="expectedKind">Expected step kind</param>
    /// <param name="expectedCategory">Expected category (e.g., "HTTP")</param>
    /// <param name="expectedDisplayName">Expected display name</param>
    /// <param name="expectedDescription">Expected description</param>
    public static void AssertStepAttributes(
        this StepTestFixture fixture,
        string expectedNamespace,
        StepKind expectedKind,
        string? expectedCategory = null,
        string? expectedDisplayName = null,
        string? expectedDescription = null
        )
    {
        var stepType = fixture.Step.GetType();
        var stepAttribute = stepType.GetCustomAttributes(typeof(StepAttribute), false)
            .Cast<StepAttribute>().FirstOrDefault();

        Assert.NotNull(stepAttribute);
        Assert.Equal(expectedNamespace, stepAttribute.Namespace);
        Assert.Equal(expectedKind, stepAttribute.Kind);

        if (expectedCategory != null) Assert.Equal(expectedCategory, stepAttribute.Category);
        if (expectedDescription != null) Assert.Equal(expectedDescription, stepAttribute.Description);
        if (expectedDisplayName != null) Assert.Equal(expectedDisplayName, stepAttribute.DisplayName);
    }
}
