using Stomme.Engine.Common;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Pipelines.PipelineExecution;
using Stomme.Engine.Pipelines.State;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Middleware.Pipelines;

/// <summary>
/// Adds extension methods to <see cref="ExceptionHandlingMiddleware"/>.
/// </summary>
public static class EngineExceptionHandlingMiddlewareExtensions
{
    /// <summary>
    /// Installs the <see cref="ExceptionHandlingMiddleware"/> component in the step execution pipeline.
    /// </summary>
    public static IPipelineExecutionPipelineBuilder UseEngineExceptionHandling(this IPipelineExecutionPipelineBuilder pipelineBuilder) => pipelineBuilder.UseMiddleware<EngineExceptionHandlingMiddleware>();
}

/// <summary>
/// Catches any exceptions thrown by downstream components and transitions the pipeline into the faulted state.
/// </summary>
public class EngineExceptionHandlingMiddleware(PipelineMiddlewareDelegate next, ISystemClock systemClock, ILogger<EngineExceptionHandlingMiddleware> logger) : IPipelineExecutionMiddleware
{
    /// <inheritdoc />
    public async ValueTask InvokeAsync(PipelineExecutionContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "An exception was caught from a downstream middleware component");
            var exceptionState = ExceptionState.FromException(e);
            var now = systemClock.UtcNow;
            var step = context.Pipeline;
            var incident = new StepIncident(step.Id, step.NodeId, step.Type, e.Message, exceptionState, now);
            
            // No state change as the pipeline / steps status should be leading.
            // We will however be adding an incident to make the issue visible.
            context.Incidents.Add(incident);
            context.AddExecutionLogEntry("Faulted", e.Message, exceptionState);
        }
    }
}