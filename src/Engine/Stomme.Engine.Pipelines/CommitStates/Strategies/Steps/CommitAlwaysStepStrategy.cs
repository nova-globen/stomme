using System.ComponentModel;

namespace Stomme.Engine.Pipelines.CommitStates.Strategies;

/// <summary>
/// Represents a strategy that always commits the pipeline whenever the associated step is about to execute or has executed.
/// This strategy ensures that the pipeline's state is persisted at both pre-execution and post-execution stages of the step.
/// </summary>
[DisplayName("Commit Before And After")]
[Description("Always commit the pipeline state when the step is about to execute or has executed.")]
public class CommitAlwaysStepStrategy : IStepCommitStrategy
{
    public CommitAction ShouldCommit(StepCommitStateStrategyContext context)
    {
        return CommitAction.Commit;
    }
}