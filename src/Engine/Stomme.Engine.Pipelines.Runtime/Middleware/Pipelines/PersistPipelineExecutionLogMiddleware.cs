using Stomme.Engine.Pipelines.Pipelines.PipelineExecution;

namespace Stomme.Engine.Pipelines.Runtime.Middleware.Pipelines;

/// <summary>
/// Takes care of persisting pipeline execution log entries.
/// </summary>
[Obsolete("This middleware is no longer used and will be removed in a future version. Execution logs are now persisted through the commit state handler.")]
public class PersistPipelineExecutionLogMiddleware(PipelineMiddlewareDelegate next) : PipelineExecutionMiddleware(next)
{
    /// <inheritdoc />
    public override async ValueTask InvokeAsync(PipelineExecutionContext context)
    {
        // Invoke next middleware.
        await Next(context).ConfigureAwait(false);
    }
}