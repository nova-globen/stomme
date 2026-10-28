using Stomme.Engine.Pipelines.Models;
using Stomme.Engine.Pipelines.Pipelines.StepExecution;
using JetBrains.Annotations;

namespace Stomme.Engine.Pipelines.Middleware.Steps;

/// <summary>
/// Adds extension methods to <see cref="ExecutionLogMiddleware"/>.
/// </summary>
public static class ExecutionLogMiddlewareExtensions
{
    /// <summary>
    /// Installs the <see cref="ExecutionLogMiddleware"/> component in the step execution pipeline.
    /// </summary>
    public static IStepExecutionPipelineBuilder UseExecutionLogging(this IStepExecutionPipelineBuilder pipelineBuilder) => pipelineBuilder.UseMiddleware<ExecutionLogMiddleware>();
}

/// <summary>
/// A step execution middleware component that extracts execution details as <see cref="PipelineExecutionLogEntry"/> objects.
/// </summary>
[UsedImplicitly]
public class ExecutionLogMiddleware(StepMiddlewareDelegate next) : IStepExecutionMiddleware
{
    /// <inheritdoc />
    public async ValueTask InvokeAsync(StepExecutionContext context)
    {
        context.AddExecutionLogEntry(IsStepBookmarked(context) ? "Resumed" : "Started");

        try
        {
            await next(context).ConfigureAwait(false);

            if (context.Status == StepStatus.Running)
            {
                if (IsStepBookmarked(context))
                    context.AddExecutionLogEntry("Suspended");
            }
        }
        catch (Exception exception)
        {
            context.AddExecutionLogEntry("Faulted",
                message: exception.Message,
                payload: new
                {
                    Exception = exception.GetType().FullName,
                    exception.Message,
                    exception.Source,
                    exception.Data,
                    exception.StackTrace,
                    InnerException = exception.InnerException?.GetType().FullName,
                });

            throw;
        }
    }

    private static bool IsStepBookmarked(StepExecutionContext context)
    {
        return context.PipelineExecutionContext.Bookmarks.Any(b => b.StepNodeId.Equals(context.StepNode.NodeId));
    }
}