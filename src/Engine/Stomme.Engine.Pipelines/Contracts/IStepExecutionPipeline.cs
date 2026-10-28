using Stomme.Engine.Pipelines.Pipelines.StepExecution;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Represents a pipeline that can be used to execute a step.
/// </summary>
public interface IStepExecutionPipeline
{
    /// <summary>
    /// Sets up the pipeline.
    /// </summary>
    /// <param name="setup">The callback that configures the pipeline.</param>
    /// <returns>The final <see cref="StepMiddlewareDelegate"/> delegate.</returns>
    StepMiddlewareDelegate Setup(Action<IStepExecutionPipelineBuilder> setup);
    
    /// <summary>
    /// The pipeline delegate.
    /// </summary>
    StepMiddlewareDelegate Pipeline { get; }
    
    /// <summary>
    /// Executes the pipeline.
    /// </summary>
    /// <param name="context">The step execution context.</param>
    Task ExecuteAsync(StepExecutionContext context);
}