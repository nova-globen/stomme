using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Pipelines.StepExecution;
using Stomme.Engine.Pipelines.Runtime.Middleware;
using Stomme.Engine.Pipelines.Runtime.Middleware.Steps;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Adds extensions to <see cref="IStepExecutionPipelineBuilder"/>.
/// </summary>
public static class StepExecutionPipelineBuilderExtensions
{
    /// <summary>
    /// Installs the <see cref="BackgroundStepInvokerMiddleware"/>.
    /// </summary>
    public static IStepExecutionPipelineBuilder UseBackgroundStepInvoker(this IStepExecutionPipelineBuilder pipelineBuilder) => pipelineBuilder.UseMiddleware<BackgroundStepInvokerMiddleware>();

    /// <summary>
    /// Installs the <see cref="EvaluateLogPersistenceModesMiddleware"/> which evaluates log persistence modes during step execution.
    /// </summary>
    public static IStepExecutionPipelineBuilder UseLogPersistenceModeEvaluation(this IStepExecutionPipelineBuilder pipelineBuilder) => pipelineBuilder.UseMiddleware<EvaluateLogPersistenceModesMiddleware>();
}