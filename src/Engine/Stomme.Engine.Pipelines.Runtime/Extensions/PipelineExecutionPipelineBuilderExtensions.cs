using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Middleware.Pipelines;
using Stomme.Engine.Pipelines.Pipelines.PipelineExecution;
using Stomme.Engine.Pipelines.Runtime.Middleware.Pipelines;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Provides extensions to <see cref="IPipelineExecutionPipelineBuilder"/> that add various middleware components.
/// </summary>
public static class PipelineExecutionPipelineBuilderExtensions
{
    /// <summary>
    /// Configures the pipeline execution pipeline with commonly used components.
    /// </summary>
    public static IPipelineExecutionPipelineBuilder UseDefaultPipeline(this IPipelineExecutionPipelineBuilder pipelineBuilder) =>
        pipelineBuilder
            .Reset()
            .UsePipelineHeartbeat()
            .UseEngineExceptionHandling()
            .UsePersistentVariables()
            .UseExceptionHandling()
            .UseDefaultStepScheduler();

    /// <summary>
    /// Installs middleware that persists the pipeline instance before and after pipeline execution.
    /// </summary>
    public static IPipelineExecutionPipelineBuilder UsePersistentVariables(this IPipelineExecutionPipelineBuilder pipelineBuilder) => pipelineBuilder.UseMiddleware<PersistentVariablesMiddleware>();
    
    public static IPipelineExecutionPipelineBuilder UsePipelineHeartbeat(this IPipelineExecutionPipelineBuilder pipelineBuilder) => pipelineBuilder.UseMiddleware<PipelineHeartbeatMiddleware>();

    /// <summary>
    /// Installs middleware that persists bookmarks after pipeline execution.
    /// </summary>
    [Obsolete("This middleware is no longer used and will be removed in a future version. Bookmarks are now persisted through the commit state handler.")]
    public static IPipelineExecutionPipelineBuilder UseBookmarkPersistence(this IPipelineExecutionPipelineBuilder pipelineBuilder) => pipelineBuilder.UseMiddleware<PersistBookmarkMiddleware>();

    /// <summary>
    /// Installs middleware that persists the pipeline execution journal.
    /// </summary>
    [Obsolete("This middleware is no longer used and will be removed in a future version. Execution logs are now persisted through the commit state handler.")]
    public static IPipelineExecutionPipelineBuilder UsePipelineExecutionLogPersistence(this IPipelineExecutionPipelineBuilder pipelineBuilder) => pipelineBuilder.UseMiddleware<PersistPipelineExecutionLogMiddleware>();

    /// <summary>
    /// Installs middleware that persists step execution records.
    /// </summary>
    [Obsolete("This middleware is no longer used and will be removed in a future version. Step state is now persisted through the commit state handler.")]
    public static IPipelineExecutionPipelineBuilder UseStepExecutionLogPersistence(this IPipelineExecutionPipelineBuilder pipelineBuilder) => pipelineBuilder.UseMiddleware<PersistStepExecutionLogMiddleware>();
}