namespace Stomme.Engine.Pipelines.Models;

/// <summary>
/// Provides pipeline options.
/// </summary>
public class PipelineOptions
{
    /// <summary>
    /// The type of <see cref="IPipelineActivationStrategy"/> to apply when new instances are requested to be created.
    /// </summary>
    public Type? ActivationStrategyType { get; set; }

    /// <summary>
    /// Used to decide if the pipeline can be used as a step.
    /// </summary>
    public bool? UsableAsStep { get; set; }
    
    /// <summary>
    /// Used to decide if the consuming pipelines should be updated automatically to use the last published version of the pipeline when it is published.
    /// </summary>
    public bool AutoUpdateConsumingPipelines { get; set; }
    
    /// <summary>
    /// The category to use when the pipeline is used as a step.
    /// </summary>
    public string? StepCategory { get; set; }
    
    /// <summary>
    /// The type of <see cref="IIncidentStrategy"/> to use when a fault occurs in the pipeline.
    /// </summary>
    public Type? IncidentStrategyType { get; set; }
  
    /// <summary>
    /// The options for committing pipeline state.
    /// </summary>
    public string? CommitStrategyName { get; set; }
}