using Stomme.Engine.Common;
using Stomme.Engine.Extensions;
using Stomme.Engine.Testing.Shared;
using Stomme.Engine.Pipelines.Steps;

namespace Stomme.Engine.Pipelines.Tests.Extensions.StepExecutionContextExtensions;

public class ExecutionChainTests
{
    [Fact]
    public async Task GetExecutionChain_PreventsInfiniteLoop_WhenCircularReferenceExists()
    {
        // Arrange
        var root = new WriteLine("root");
        var fixture = new StepTestFixture(root);
        var contextRoot = await fixture.BuildAsync();
        var w = contextRoot.PipelineExecutionContext;
        var clock = contextRoot.GetRequiredService<ISystemClock>();
        
        var contextA = new StepExecutionContext("A", w, null, root, contextRoot.StepDescriptor, contextRoot.StartedAt, null, clock, default);
        var contextB = new StepExecutionContext("B", w, null, root, contextRoot.StepDescriptor, contextRoot.StartedAt, null, clock, default);

        // Create a cycle: A -> B -> A
        contextA.SchedulingStepExecutionId = "B";
        contextB.SchedulingStepExecutionId = "A";

        // Register contexts in the same pipeline execution context
        w.AddStepExecutionContext(contextA);
        w.AddStepExecutionContext(contextB);

        // Act
        var chain = contextA.GetExecutionChain().ToList();

        // Assert
        Assert.Equal(2, chain.Count);
        Assert.Equal("B", chain[0].Id);
        Assert.Equal("A", chain[1].Id);
    }
    
    [Fact]
    public async Task GetExecutionChain_ReturnsFullChain_WhenNoCycles()
    {
        // Arrange
        var root = new WriteLine("root");
        var fixture = new StepTestFixture(root);
        var contextRoot = await fixture.BuildAsync();
        var w = contextRoot.PipelineExecutionContext;
        var clock = contextRoot.GetRequiredService<ISystemClock>();
        
        var contextA = new StepExecutionContext("A", w, null, root, contextRoot.StepDescriptor, contextRoot.StartedAt, null, clock, default);
        var contextB = new StepExecutionContext("B", w, null, root, contextRoot.StepDescriptor, contextRoot.StartedAt, null, clock, default);
        var contextC = new StepExecutionContext("C", w, null, root, contextRoot.StepDescriptor, contextRoot.StartedAt, null, clock, default);

        // Chain: A -> B -> C (A is leaf, C is root)
        contextA.SchedulingStepExecutionId = "B";
        contextB.SchedulingStepExecutionId = "C";
        contextC.SchedulingStepExecutionId = null;

        // Register contexts in the same pipeline execution context
        w.AddStepExecutionContext(contextA);
        w.AddStepExecutionContext(contextB);
        w.AddStepExecutionContext(contextC);

        // Act
        var chain = contextA.GetExecutionChain().ToList();

        // Assert
        Assert.Equal(3, chain.Count);
        Assert.Equal("C", chain[0].Id);
        Assert.Equal("B", chain[1].Id);
        Assert.Equal("A", chain[2].Id);
    }
}
