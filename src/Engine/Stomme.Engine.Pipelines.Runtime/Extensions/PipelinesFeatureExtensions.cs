



using Stomme.Engine.Pipelines.Middleware.Steps;
// ReSharper disable once CheckNamespace
using Stomme.Engine.Pipelines.Features;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Adds an extension method to the <see cref="PipelinesFeature"/> that installs a default pipeline runtime execution pipeline.
/// </summary>
public static class PipelinesFeatureExtensions
{
     /// <summary>
     /// Installs a default pipeline runtime execution pipeline.
     /// </summary>
     public static PipelinesFeature WithDefaultPipelineExecutionPipeline(this PipelinesFeature pipelinesFeature) => 
         pipelinesFeature.WithPipelineExecutionPipeline(executionPipeline => 
             executionPipeline.UseDefaultPipeline());

     /// <summary>
     /// Installs a step invoker that can run steps asynchronously in the background.
     /// </summary>
     public static PipelinesFeature WithBackgroundStepExecutionPipeline(this PipelinesFeature pipelinesFeature) => 
          pipelinesFeature.WithStepExecutionPipeline(executionPipeline => 
              executionPipeline
                  .UseExceptionHandling()
                  .UseLogPersistenceModeEvaluation()
                  .UseExecutionLogging()
                  .UseNotifications()
                  .UseBackgroundStepInvoker());
}