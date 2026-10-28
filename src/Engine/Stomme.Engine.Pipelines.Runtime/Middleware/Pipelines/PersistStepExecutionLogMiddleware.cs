using Stomme.Engine.Pipelines.Pipelines.PipelineExecution;

namespace Stomme.Engine.Pipelines.Runtime.Middleware.Pipelines;

/// <summary>
/// Creates and updates step execution records from step execution contexts.
/// </summary>
[Obsolete("This middleware is no longer used and will be removed in a future version. Step state is now persisted through the commit state handler")]
public class PersistStepExecutionLogMiddleware(PipelineMiddlewareDelegate next) : PipelineExecutionMiddleware(next)
{
    /// <inheritdoc />
    public override async ValueTask InvokeAsync(PipelineExecutionContext context)
    {
        await Next(context).ConfigureAwait(false);
    }
}