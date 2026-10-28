using System.ComponentModel;

namespace Stomme.Engine.Pipelines.CommitStates.Strategies;

/// <summary>
/// Represents a pipeline commit strategy that commits changes when the pipeline has been executed.
/// </summary>
[DisplayName("Commit After Pipeline Execution")]
[Description("Commit the pipeline state when the pipeline has been executed.")]
public class PipelineExecutedPipelineStrategy : IPipelineCommitStrategy
{
    public CommitAction ShouldCommit(PipelineCommitStateStrategyContext context)
    {
        return context.LifetimeEvent == PipelineLifetimeEvent.PipelineExecuted ? CommitAction.Commit : CommitAction.Default;
    }
}