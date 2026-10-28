using Stomme.Engine.Pipelines.Pipelines.PipelineExecution;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Represents a pipeline execution pipeline.
/// </summary>
public interface IPipelineExecutionPipeline
{
    /// <summary>
    /// The pipeline builder factory.
    /// </summary>
    Action<IPipelineExecutionPipelineBuilder> ConfigurePipelineBuilder { get; }
    
    /// <summary>
    /// Sets up the pipeline using the specified pipeline builder factory.
    /// </summary>
    PipelineMiddlewareDelegate Setup(Action<IPipelineExecutionPipelineBuilder> setup);
    
    /// <summary>
    /// The constructed pipeline delegate.
    /// </summary>
    PipelineMiddlewareDelegate Pipeline { get; }
    
    /// <summary>
    /// Executes the pipeline with the specified pipeline execution context.
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    Task ExecuteAsync(PipelineExecutionContext context);
}