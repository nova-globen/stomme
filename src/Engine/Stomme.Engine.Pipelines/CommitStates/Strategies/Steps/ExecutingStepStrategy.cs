using System.ComponentModel;

namespace Stomme.Engine.Pipelines.CommitStates.Strategies;

/// <summary>
/// Represents a commit strategy that evaluates whether a pipeline commit should occur
/// when a step is in the "Executing" state.
/// </summary>
/// <remarks>
/// This strategy determines commit behavior based on the step's lifecycle event.
/// Specifically, it commits the pipeline if the step is currently executing
/// (i.e., the lifetime event is `StepLifetimeEvent.StepExecuting`).
/// For all other states, it defaults to no specific commit action.
/// </remarks>
[DisplayName("Commit Before")]
[Description("Commit the pipeline state when the step is executing.")]
public class ExecutingStepStrategy : IStepCommitStrategy
{
    public CommitAction ShouldCommit(StepCommitStateStrategyContext context)
    {
        return context.LifetimeEvent == StepLifetimeEvent.StepExecuting ? CommitAction.Commit : CommitAction.Default;
    }
}