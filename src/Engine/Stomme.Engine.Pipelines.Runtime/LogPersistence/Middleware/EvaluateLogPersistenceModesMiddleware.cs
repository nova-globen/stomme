using Stomme.Engine.Pipelines.Pipelines.StepExecution;

namespace Stomme.Engine.Pipelines.Runtime.Middleware;

public class EvaluateLogPersistenceModesMiddleware(StepMiddlewareDelegate next, IStepPropertyLogPersistenceEvaluator persistenceEvaluator) : IStepExecutionMiddleware
{
    public async ValueTask InvokeAsync(StepExecutionContext context)
    {
        // Evaluate log persistence mode before executing the next middleware to ensure the mode is known before any potential commit action.
        var persistenceLogMap = await persistenceEvaluator.EvaluateLogPersistenceModesAsync(context).ConfigureAwait(false);
        context.SetLogPersistenceModeMap(persistenceLogMap);
        
        // Execute next middleware.
        await next(context).ConfigureAwait(false);
    }
}