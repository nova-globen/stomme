using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Pipelines.StepExecution;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Middleware.Steps;

/// <summary>
/// Adds extension methods to <see cref="ExceptionHandlingMiddleware"/>.
/// </summary>
public static class ExceptionHandlingMiddlewareExtensions
{
    /// <summary>
    /// Installs the <see cref="ExceptionHandlingMiddleware"/> component in the step execution pipeline.
    /// </summary>
    public static IStepExecutionPipelineBuilder UseExceptionHandling(this IStepExecutionPipelineBuilder pipelineBuilder) => pipelineBuilder.UseMiddleware<ExceptionHandlingMiddleware>();
}

/// <summary>
/// Catches any exceptions thrown by downstream components and transitions the pipeline into the faulted state.
/// </summary>
public class ExceptionHandlingMiddleware(StepMiddlewareDelegate next, IIncidentStrategyResolver incidentStrategyResolver, ILogger<ExceptionHandlingMiddleware> logger)
    : IStepExecutionMiddleware
{
    /// <inheritdoc />
    public async ValueTask InvokeAsync(StepExecutionContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "An exception was caught from a downstream middleware component");
            context.Fault(e);
            await HandleIncidentAsync(context).ConfigureAwait(false);
        }
    }

    private async Task HandleIncidentAsync(StepExecutionContext context)
    {
        var strategy = await incidentStrategyResolver.ResolveStrategyAsync(context).ConfigureAwait(false);
        strategy.HandleIncident(context);
    }
}