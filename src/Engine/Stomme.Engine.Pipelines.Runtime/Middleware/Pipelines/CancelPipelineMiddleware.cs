using Stomme.Engine.Pipelines.Pipelines.PipelineExecution;

namespace Stomme.Engine.Pipelines.Runtime.Middleware.Pipelines;

/// <summary>
/// Represents a middleware that cancels the pipeline.
/// </summary>
public class CancelPipelineMiddleware(PipelineMiddlewareDelegate next) : PipelineExecutionMiddleware(next)
{
    /// <inheritdoc />
    public override ValueTask InvokeAsync(PipelineExecutionContext context)
    {
        context.Cancel();
        return ValueTask.CompletedTask;
    }
}