namespace Stomme.Engine.Pipelines.CommitStates;

/// <summary>
/// Configuration options for commit state strategies.
/// </summary>
public class CommitStateOptions
{
    /// <summary>
    /// Gets or sets the pipeline commit strategies.
    /// </summary>
    public IDictionary<string, PipelineCommitStrategyRegistration> PipelineCommitStrategies { get; set; } = new Dictionary<string, PipelineCommitStrategyRegistration>();

    /// <summary>
    /// Gets or sets the step commit strategies.
    /// </summary>
    public IDictionary<string, StepCommitStrategyRegistration> StepCommitStrategies { get; set; } = new Dictionary<string, StepCommitStrategyRegistration>();

    /// <summary>
    /// Gets or sets the default pipeline commit strategy instance to use when a pipeline does not specify its own.
    /// This strategy is not added to the registry and serves only as a fallback.
    /// </summary>
    public IPipelineCommitStrategy? DefaultPipelineCommitStrategy { get; set; }

    /// <summary>
    /// Gets or sets the default step commit strategy instance to use when a step does not specify its own.
    /// This strategy is not added to the registry and serves only as a fallback.
    /// </summary>
    public IStepCommitStrategy? DefaultStepCommitStrategy { get; set; }
}