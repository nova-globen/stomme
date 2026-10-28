using Stomme.Engine.Pipelines.Pipelines.PipelineExecution;

namespace Stomme.Engine.Pipelines.Runtime.Middleware.Pipelines;

/// <summary>
/// Takes care of loading and persisting pipeline variables.
/// </summary>
public class PersistentVariablesMiddleware(PipelineMiddlewareDelegate next, IVariablePersistenceManager variablePersistenceManager) : PipelineExecutionMiddleware(next)
{
    /// <inheritdoc />
    public override async ValueTask InvokeAsync(PipelineExecutionContext context)
    {
        // Load variables into the pipeline execution context.
        await variablePersistenceManager.LoadVariablesAsync(context).ConfigureAwait(false);
        
        // Invoke next middleware.
        await Next(context).ConfigureAwait(false);
        
        // Variables are persisted through the commit state handler.
    }
}