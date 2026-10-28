using System.ComponentModel;

namespace Stomme.Engine.Pipelines.CommitStates.Strategies;

/// <summary>
/// A strategy implementation of <see cref="IStepCommitStrategy"/> that specifies
/// the pipeline should never commit when the associated step is executed or has executed.
/// This overrides any pipeline-level commit strategy.
/// </summary>
[DisplayName("Commit Later")]
[Description("Pipeline state will not be commited before or after the step executes.")]
public class CommitNeverStepStrategy : IStepCommitStrategy
{
    public CommitAction ShouldCommit(StepCommitStateStrategyContext context)
    {
        return CommitAction.Skip;
    }
}