using System.ComponentModel;

namespace Stomme.Engine.Pipelines.CommitStates.Strategies;

/// <summary>
/// Represents a pipeline commit strategy that determines whether a commit should occur
/// during the StepExecuting lifecycle event of a step.
/// </summary>
/// <remarks>
/// This strategy evaluates the pipeline execution context and checks if the current lifecycle event is
/// <see cref="PipelineLifetimeEvent.StepExecuting"/>. If the condition is met, the strategy indicates
/// that a commit action should be performed. Otherwise, a default action will be returned.
/// </remarks>
[DisplayName("Commit Before Step Execution")]
[Description("Determines whether a pipeline state should be committed if the current step is executing.")]
public class StepExecutingPipelineStrategy : IPipelineCommitStrategy
{
    public CommitAction ShouldCommit(PipelineCommitStateStrategyContext context)
    {
        return context.LifetimeEvent == PipelineLifetimeEvent.StepExecuting ? CommitAction.Commit : CommitAction.Default;
    }
}