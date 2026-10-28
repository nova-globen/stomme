using Stomme.Engine.Common;
using Stomme.Engine.Testing.Shared;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Management.Options;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Stomme.Engine.Pipelines.Runtime.Tests.Services;

/// <summary>
/// Unit tests for DefaultStepExecutionMapper.
/// </summary>
public class DefaultStepExecutionMapperTests
{
    /// <summary>
    /// Tests that the mapper correctly maps CallStackDepth, SchedulingStepExecutionId,
    /// and SchedulingStepId from the StepExecutionContext to the record.
    /// </summary>
    [Fact]
    public async Task MapAsync_MapsCallStackDepth_Correctly()
    {
        // Arrange
        var safeSerializer = Substitute.For<ISafeSerializer>();
        safeSerializer.Serialize(Arg.Any<object>()).Returns("serialized");
        
        var payloadSerializer = Substitute.For<IPayloadSerializer>();
        payloadSerializer.Serialize(Arg.Any<object>()).Returns("serialized");
        
        var compressionCodecResolver = Substitute.For<ICompressionCodecResolver>();
        var compressionCodec = Substitute.For<ICompressionCodec>();
        compressionCodecResolver.Resolve(Arg.Any<string>()).Returns(compressionCodec);
        compressionCodec.CompressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<string>("compressed"));
        
        var managementOptions = Substitute.For<IOptions<ManagementOptions>>();
        managementOptions.Value.Returns(new ManagementOptions());
        
        var mapper = new DefaultStepExecutionMapper(
            safeSerializer,
            payloadSerializer,
            compressionCodecResolver,
            managementOptions);
        
        // Use StepTestFixture to create real context objects
        var step = new WriteLine("Test");
        var fixture = new StepTestFixture(step);
        var stepExecutionContext = await fixture.BuildAsync();
        
        // Set the properties we want to test
        stepExecutionContext.CallStackDepth = 2;
        stepExecutionContext.SchedulingStepExecutionId = "context-b";
        stepExecutionContext.SchedulingStepId = "step-b";
        
        // Act
        var record = await mapper.MapAsync(stepExecutionContext);
        
        // Assert
        Assert.NotNull(record);
        Assert.Equal(2, record.CallStackDepth);
        Assert.Equal("context-b", record.SchedulingStepExecutionId);
        Assert.Equal("step-b", record.SchedulingStepId);
    }
}
