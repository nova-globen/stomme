using Stomme.Engine.Extensions;
using static Stomme.Engine.Pipelines.Tests.Extensions.StepExecutionContextExtensions.TestHelpers;

namespace Stomme.Engine.Pipelines.Tests.Extensions.StepExecutionContextExtensions;

public class FaultHandlingTests
{
    [Fact]
    public async Task Fault_SetsExceptionAndStatus()
    {
        // Arrange
        var context = await CreateContextAsync();
        var exception = new InvalidOperationException("Test error");

        // Act
        context.Fault(exception);

        // Assert
        Assert.Equal(StepStatus.Faulted, context.Status);
        Assert.Equal(exception, context.Exception);
        Assert.Equal(1, context.AggregateFaultCount);
    }

    [Fact]
    public async Task RecoverFromFault_ResetsFaultCount()
    {
        // Arrange
        var context = await CreateContextAsync();
        var exception = new InvalidOperationException("Test error");
        context.Fault(exception);

        // Act
        context.RecoverFromFault();

        // Assert
        Assert.Equal(0, context.AggregateFaultCount);
        Assert.Equal(StepStatus.Running, context.Status);
    }
}
