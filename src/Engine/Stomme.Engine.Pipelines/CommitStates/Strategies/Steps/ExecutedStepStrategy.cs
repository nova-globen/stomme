using System.ComponentModel;

namespace Stomme.Engine.Pipelines.CommitStates.Strategies;

/// <summary>
/// Represents a step commit strategy that determines whether a commit action should occur
/// after a step has executed within a pipeline.
/// </summary>
/// <remarks>
/// This strategy evaluates the step's execution events, specifically committing only if the
/// step's lifetime event is "StepExecuted".
/// </remarks>
[DisplayName("Commit After")]
[Description("Commit the pipeline state after the step has executed.")]
public class ExecutedStepStrategy : IStepCommitStrategy
{
    public CommitAction ShouldCommit(StepCommitStateStrategyContext context)
    {
        return context.LifetimeEvent == StepLifetimeEvent.StepExecuted ? CommitAction.Commit : CommitAction.Default;
    }
}