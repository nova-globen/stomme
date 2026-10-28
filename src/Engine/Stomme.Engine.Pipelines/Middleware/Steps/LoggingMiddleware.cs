using System.Diagnostics;
using Stomme.Engine.Pipelines.Pipelines.StepExecution;
using Microsoft.Extensions.Logging;

namespace Stomme.Engine.Pipelines.Middleware.Steps;

/// <summary>
/// A step execution middleware component that logs information about the step being executed.
/// </summary>
public class LoggingMiddleware : IStepExecutionMiddleware
{
    private readonly StepMiddlewareDelegate _next;
    private readonly ILogger _logger;
    private readonly Stopwatch _stopwatch;

    /// <summary>
    /// Constructor.
    /// </summary>
    public LoggingMiddleware(StepMiddlewareDelegate next, ILogger<LoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _stopwatch = new();
    }

    /// <inheritdoc />
    public async ValueTask InvokeAsync(StepExecutionContext context)
    {
        if (!_logger.IsEnabled(LogLevel.Information))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var step = context.Step;
        _logger.LogInformation("Executing step {StepId}", step.Id);
        _stopwatch.Restart();
        await _next(context).ConfigureAwait(false);
        _stopwatch.Stop();
        _logger.LogInformation("Executed step {StepId} in {Elapsed}", step.Id, _stopwatch.Elapsed);
    }
}

/// <summary>
/// Extends <see cref="IStepExecutionPipelineBuilder"/> to install the <see cref="LoggingMiddleware"/> component.
/// </summary>
public static class LoggingMiddlewareExtensions
{
    /// <summary>
    /// Installs the <see cref="LoggingMiddleware"/> component.
    /// </summary>
    public static IStepExecutionPipelineBuilder UseLogging(this IStepExecutionPipelineBuilder pipelineBuilder) => pipelineBuilder.UseMiddleware<LoggingMiddleware>();
}