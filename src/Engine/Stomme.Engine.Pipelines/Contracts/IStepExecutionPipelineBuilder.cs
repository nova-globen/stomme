using Stomme.Engine.Pipelines.Pipelines.StepExecution;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Builds a step execution pipeline.
/// </summary>
public interface IStepExecutionPipelineBuilder
{
    /// <summary>
    /// The current service provider to resolve services from.
    /// </summary>
    IServiceProvider ServiceProvider { get; }
    
    /// <summary>
    /// Installs the specified delegate as a middleware component.
    /// </summary>
    /// <param name="middleware">The middleware delegate to install.</param>
    /// <returns>The current <see cref="IStepExecutionPipelineBuilder"/>.</returns>
    IStepExecutionPipelineBuilder Use(Func<StepMiddlewareDelegate, StepMiddlewareDelegate> middleware);
    
    /// <summary>
    /// Inserts the middleware component at the specified index.
    /// </summary>
    IStepExecutionPipelineBuilder Insert(int index, Func<StepMiddlewareDelegate, StepMiddlewareDelegate> middleware);
    
    /// <summary>
    /// Constructs the final <see cref="StepMiddlewareDelegate"/> delegate that invokes each installed middleware component.
    /// </summary>
    /// <returns>The final <see cref="StepMiddlewareDelegate"/> delegate.</returns>
    public StepMiddlewareDelegate Build();
    
    /// <summary>
    /// Clears the current pipeline.
    /// </summary>
    IStepExecutionPipelineBuilder Reset();
}