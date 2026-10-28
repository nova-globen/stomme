using Stomme.Engine.Common;
using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Pipelines.PipelineExecution;
using Stomme.Engine.Pipelines.State;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Middleware.Pipelines;

/// <summary>
/// Adds extension methods to <see cref="ExceptionHandlingMiddleware"/>.
/// </summary>
public static class ExceptionHandlingMiddlewareExtensions
{
    /// <summary>
    /// Installs the <see cref="ExceptionHandlingMiddleware"/> component in the step execution pipeline.
    /// </summary>
    public static IPipelineExecutionPipelineBuilder UseExceptionHandling(this IPipelineExecutionPipelineBuilder pipelineBuilder) => pipelineBuilder.UseMiddleware<ExceptionHandlingMiddleware>();
}

/// <summary>
/// Catches any exceptions thrown by downstream components and transitions the pipeline into the faulted state.
/// </summary>
public class ExceptionHandlingMiddleware : IPipelineExecutionMiddleware
{
    private readonly PipelineMiddlewareDelegate _next;
    private readonly ISystemClock _systemClock;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    /// <summary>
    /// Constructor.
    /// </summary>
    public ExceptionHandlingMiddleware(PipelineMiddlewareDelegate next, ISystemClock systemClock, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _systemClock = systemClock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask InvokeAsync(PipelineExecutionContext context)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "An exception was caught from a downstream middleware component");
            var exceptionState = ExceptionState.FromException(e);
            var now = _systemClock.UtcNow;
            var step = context.Pipeline;
            var incident = new StepIncident(step.Id, step.NodeId ,step.Type, e.Message, exceptionState, now);
            context.Incidents.Add(incident);
            context.TransitionTo(PipelineSubStatus.Faulted);
            context.AddExecutionLogEntry("Faulted", e.Message, exceptionState);
        }
    }
}