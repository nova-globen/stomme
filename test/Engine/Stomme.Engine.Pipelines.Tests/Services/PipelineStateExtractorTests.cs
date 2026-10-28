using Stomme.Engine.Common;
using Stomme.Engine.Testing.Shared;
using Stomme.Engine.Pipelines.Steps;
using Stomme.Engine.Pipelines.Options;
using Stomme.Engine.Pipelines.Services;
using Stomme.Engine.Pipelines.State;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stomme.Engine.Pipelines.Tests.Services;

public class PipelineStateExtractorTests
{
    [Fact]
    public async Task Extract_And_Apply_PreservesCallStackDepth()
    {
        // Arrange
        var root = new WriteLine("root");
        var fixture = new StepTestFixture(root);
        var contextRoot = await fixture.BuildAsync();
        var pipelineExecutionContext = contextRoot.PipelineExecutionContext;
        
        var contextA = await pipelineExecutionContext.CreateStepExecutionContextAsync(root);
        contextA.CallStackDepth = 10; // Manually set for testing persistence
        pipelineExecutionContext.AddStepExecutionContext(contextA);

        var extractor = pipelineExecutionContext.GetRequiredService<IPipelineStateExtractor>();

        // Act
        var state = extractor.Extract(pipelineExecutionContext);
        
        // Create a new context to apply the state to
        var newPipelineExecutionContext = await PipelineExecutionContext.CreateAsync(
            pipelineExecutionContext.ServiceProvider,
            pipelineExecutionContext.PipelineGraph,
            state.Id,
            CancellationToken.None
        );
        
        await extractor.ApplyAsync(newPipelineExecutionContext, state);

        // Assert
        var restoredContextA = newPipelineExecutionContext.StepExecutionContexts.FirstOrDefault(x => x.Id == contextA.Id);
        Assert.NotNull(restoredContextA);
        Assert.Equal(10, restoredContextA.CallStackDepth);
    }

    [Fact]
    public async Task CallStackDepth_IsIncrementedWhenSchedulingCallStackDepthProvided()
    {
        // Arrange
        var root = new WriteLine("root");
        var fixture = new StepTestFixture(root);
        var contextRoot = await fixture.BuildAsync();
        var pipelineExecutionContext = contextRoot.PipelineExecutionContext;

        var schedulingDepth = 5;
        var options = new StepInvocationOptions
        {
            SchedulingStepExecutionId = "parent-step-id",
            SchedulingPipelineInstanceId = "parent-pipeline-id",
            SchedulingCallStackDepth = schedulingDepth
        };

        // Act - Create a new step context with scheduling information
        var context = await pipelineExecutionContext.CreateStepExecutionContextAsync(root, options);

        // Assert - The CallStackDepth should be incremented from the scheduling depth
        Assert.Equal(schedulingDepth + 1, context.CallStackDepth);
        Assert.Equal("parent-step-id", context.SchedulingStepExecutionId);
        Assert.Equal("parent-pipeline-id", context.SchedulingPipelineInstanceId);
    }

    [Fact]
    public async Task CallStackDepth_IsIncrementedFromParentContext()
    {
        // Arrange
        var root = new WriteLine("root");
        var fixture = new StepTestFixture(root);
        var contextRoot = await fixture.BuildAsync();
        var pipelineExecutionContext = contextRoot.PipelineExecutionContext;

        // Create a parent context with depth 3
        var parentContext = await pipelineExecutionContext.CreateStepExecutionContextAsync(root);
        parentContext.CallStackDepth = 3;
        pipelineExecutionContext.AddStepExecutionContext(parentContext);

        var options = new StepInvocationOptions
        {
            SchedulingStepExecutionId = parentContext.Id
        };

        // Act - Create a child context that references the parent
        var childContext = await pipelineExecutionContext.CreateStepExecutionContextAsync(root, options);

        // Assert - The CallStackDepth should be parent depth + 1
        Assert.Equal(4, childContext.CallStackDepth);
        Assert.Equal(parentContext.Id, childContext.SchedulingStepExecutionId);
    }
}
