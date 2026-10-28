using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Features;
using Stomme.Engine.Pipelines.Middleware.Steps;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Adds an extension method to the <see cref="PipelinesFeature"/> that installs a default pipeline runtime execution pipeline.
/// </summary>
public static class PipelinePipelinesFeatureExtensions
{
    /// <summary>
    /// Installs a default pipeline runtime execution pipeline.
    /// </summary>
    public static PipelinesFeature WithDefaultPipelineExecutionPipeline(this PipelinesFeature pipelinesFeature, Action<IPipelineExecutionPipelineBuilder>? configurePipeline = null)
    {
        return pipelinesFeature.WithPipelineExecutionPipeline(executionPipeline =>
        {
            executionPipeline.UseDefaultPipeline();
            configurePipeline?.Invoke(executionPipeline);
        });
    }

    /// <summary>
    /// Installs a step invoker that can run steps asynchronously in the background.
    /// </summary>
    public static PipelinesFeature WithDefaultStepExecutionPipeline(this PipelinesFeature pipelinesFeature, Action<IStepExecutionPipelineBuilder>? configurePipeline = null)
    {
        return pipelinesFeature.WithStepExecutionPipeline(executionPipeline =>
        {
            executionPipeline
                .UseExceptionHandling()
                .UseExecutionLogging()
                .UseNotifications()
                .UseLogPersistenceModeEvaluation()
                .UseBackgroundStepInvoker();

            configurePipeline?.Invoke(executionPipeline);
        });
    }
}