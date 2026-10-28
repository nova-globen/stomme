using Stomme.Engine.Pipelines.Pipelines.PipelineExecution;

namespace Stomme.Engine.Pipelines;

/// <summary>
/// Builds a pipeline execution pipeline.
/// </summary>
public interface IPipelineExecutionPipelineBuilder
{
    /// <summary>
    /// A general-purpose dictionary of values that can be used by middleware components.
    /// </summary>
    public IDictionary<object, object?> Properties { get; }
    
    /// <summary>
    /// The middleware components that have been installed.
    /// </summary>
    public IEnumerable<Func<PipelineMiddlewareDelegate, PipelineMiddlewareDelegate>> Components { get; }
    
    /// <summary>
    /// The current service provider to resolve services from.
    /// </summary>
    IServiceProvider ServiceProvider { get; }
    
    /// <summary>
    /// Installs the specified delegate as a middleware component.
    /// </summary>
    IPipelineExecutionPipelineBuilder Use(Func<PipelineMiddlewareDelegate, PipelineMiddlewareDelegate> middleware);
    
    /// <summary>
    /// Constructs the final <see cref="PipelineMiddlewareDelegate"/> delegate that invokes each installed middleware component.
    /// </summary>
    public PipelineMiddlewareDelegate Build();
    
    /// <summary>
    /// Clears the current pipeline.
    /// </summary>
    IPipelineExecutionPipelineBuilder Reset();
    
    /// <summary>
    /// Inserts the middleware component at the specified index.
    /// </summary>
    IPipelineExecutionPipelineBuilder Insert(int index, Func<PipelineMiddlewareDelegate, PipelineMiddlewareDelegate> middleware);

    /// <summary>
    /// Replaces the middleware component at the specified index with the specified delegate.
    /// </summary>
    /// <param name="index">The index of the middleware component to replace.</param>
    /// <param name="middleware">The delegate to use as the new middleware component.</param>
    /// <returns>A new instance of <see cref="IPipelineExecutionPipelineBuilder"/> with the middleware component replaced.</returns>
    IPipelineExecutionPipelineBuilder Replace(int index, Func<PipelineMiddlewareDelegate, PipelineMiddlewareDelegate> middleware);
}