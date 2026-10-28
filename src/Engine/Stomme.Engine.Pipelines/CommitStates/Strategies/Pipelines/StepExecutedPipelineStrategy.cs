using System.ComponentModel;

namespace Stomme.Engine.Pipelines.CommitStates.Strategies;

/// <summary>
/// Represents a commit strategy that determines whether a pipeline state should be committed
/// based on the "StepExecuted" lifetime event of a step.
/// </summary>
/// <remarks>
/// This strategy evaluates the <see cref="PipelineCommitStateStrategyContext"/> provided during execution
/// and returns a <see cref="CommitAction.Commit"/> action if the step's lifetime event corresponds
/// to <see cref="PipelineLifetimeEvent.StepExecuted"/>. Otherwise, it defaults to <see cref="CommitAction.Default"/>.
/// </remarks>
[DisplayName("Commit After Step Execution")]
[Description("Determines whether a pipeline state should be committed if the current step has executed.")]
public class StepExecutedPipelineStrategy : IPipelineCommitStrategy
{
    public CommitAction ShouldCommit(PipelineCommitStateStrategyContext context)
    {
        return context.LifetimeEvent == PipelineLifetimeEvent.StepExecuted ? CommitAction.Commit : CommitAction.Default;
    }
}