using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Tests.Models;

public class StepConstructionResultTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void Constructor_WithVaryingExceptionCounts_SetsPropertiesCorrectly(int exceptionCount, bool expectedHasExceptions)
    {
        // Arrange
        var step = CreateStep();
        var exceptions = CreateExceptions(exceptionCount);

        // Act
        var result = new StepConstructionResult(step, exceptions);

        // Assert
        Assert.Same(step, result.Step);
        Assert.Equal(exceptionCount, result.Exceptions.Count());
        Assert.Equal(expectedHasExceptions, result.HasExceptions);
    }

    [Fact]
    public void Constructor_WithNullExceptions_TreatsAsEmpty()
    {
        // Arrange
        var step = CreateStep();

        // Act
        var result = new StepConstructionResult(step, null);

        // Assert
        Assert.Empty(result.Exceptions);
        Assert.False(result.HasExceptions);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void Cast_PreservesStepAndExceptions(int exceptionCount, bool expectedHasExceptions)
    {
        // Arrange
        var step = CreateStep();
        var exceptions = CreateExceptions(exceptionCount);
        var result = new StepConstructionResult(step, exceptions);

        // Act
        var typedResult = result.Cast<WriteLine>();

        // Assert
        Assert.IsType<StepConstructionResult<WriteLine>>(typedResult);
        Assert.Same(step, typedResult.Step);
        Assert.Equal(exceptionCount, typedResult.Exceptions.Count());
        Assert.Equal(expectedHasExceptions, typedResult.HasExceptions);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void GenericConstructor_CreatesTypedResultWithInheritance(int exceptionCount, bool expectedHasExceptions)
    {
        // Arrange
        var step = CreateStep();
        var exceptions = CreateExceptions(exceptionCount);

        // Act
        var result = new StepConstructionResult<WriteLine>(step, exceptions);

        // Assert
        Assert.Same(step, result.Step);
        Assert.Equal(exceptionCount, result.Exceptions.Count());
        Assert.Equal(expectedHasExceptions, result.HasExceptions);
        Assert.IsAssignableFrom<StepConstructionResult>(result);
    }

    [Fact]
    public void Exceptions_CanBeEnumerated()
    {
        // Arrange
        var step = CreateStep();
        var exceptions = CreateExceptions(3);
        var result = new StepConstructionResult(step, exceptions);

        // Act & Assert
        var count = 0;
        foreach (var ex in result.Exceptions)
        {
            Assert.NotNull(ex);
            count++;
        }
        Assert.Equal(3, count);
    }

    // Helper methods
    private static WriteLine CreateStep() => new("test");

    private static List<Exception>? CreateExceptions(int count)
    {
        if (count == 0) return null;

        return Enumerable.Range(1, count)
            .Select(i => new InvalidOperationException($"Error {i}") as Exception)
            .ToList();
    }
}
