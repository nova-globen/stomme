using Stomme.Engine.Testing.Shared;
using Stomme.Engine.Pipelines.Steps;

namespace Stomme.Engine.Pipelines.Tests.Extensions.StepExecutionContextExtensions;

/// <summary>
/// Provides helper methods for StepExecutionContextExtensions unit tests.
/// </summary>
public static class TestHelpers
{
    /// <summary>
    /// Creates a StepExecutionContext for testing purposes.
    /// </summary>
    /// <param name="step">Optional step to use. If null, a default WriteLine step is created.</param>
    /// <returns>A configured StepExecutionContext ready for testing.</returns>
    public static Task<StepExecutionContext> CreateContextAsync(IStep? step = null)
    {
        step ??= new WriteLine("test");
        var fixture = new StepTestFixture(step);
        return fixture.BuildAsync();
    }
}
