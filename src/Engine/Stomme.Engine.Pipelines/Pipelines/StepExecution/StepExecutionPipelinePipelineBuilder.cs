namespace Stomme.Engine.Pipelines.Pipelines.StepExecution;

/// <inheritdoc />
public class StepExecutionPipelinePipelineBuilder(IServiceProvider serviceProvider) : IStepExecutionPipelineBuilder
{
    private readonly IList<Func<StepMiddlewareDelegate, StepMiddlewareDelegate>> _components = new List<Func<StepMiddlewareDelegate, StepMiddlewareDelegate>>();

    /// <inheritdoc />
    public IServiceProvider ServiceProvider { get; } = serviceProvider;

    /// <inheritdoc />
    public IStepExecutionPipelineBuilder Use(Func<StepMiddlewareDelegate, StepMiddlewareDelegate> middleware)
    {
        _components.Add(middleware);
        return this;
    }

    /// <inheritdoc />
    public IStepExecutionPipelineBuilder Insert(int index, Func<StepMiddlewareDelegate, StepMiddlewareDelegate> middleware)
    {
        _components.Insert(index, middleware);
        return this;
    }

    /// <inheritdoc />
    public StepMiddlewareDelegate Build()
    {
        StepMiddlewareDelegate executionPipeline = _ => new ValueTask();

        foreach (var component in _components.Reverse()) 
            executionPipeline = component(executionPipeline);

        return executionPipeline;
    }

    /// <inheritdoc />
    public IStepExecutionPipelineBuilder Reset()
    {
        _components.Clear();
        return this;
    }
}