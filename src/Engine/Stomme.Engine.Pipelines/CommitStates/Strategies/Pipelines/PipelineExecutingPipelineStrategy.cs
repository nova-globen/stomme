using System.ComponentModel;

namespace Stomme.Engine.Pipelines.CommitStates.Strategies;

/// <summary>
/// Implements a commit strategy that determines whether the pipeline state
/// should be committed when the pipeline is in the "executing" lifetime event.
/// </summary>
/// <remarks>
/// This strategy checks the current context's `LifetimeEvent` and commits
/// the pipeline state if it corresponds to the `PipelineExecuting` event.
/// Otherwise, it defaults to no explicit commit action.
/// </remarks>
[DisplayName("Commit Before Pipeline Execution")]
[Description("Commit the pipeline state when the pipeline is executing.")]
public class PipelineExecutingPipelineStrategy : IPipelineCommitStrategy
{
    public CommitAction ShouldCommit(PipelineCommitStateStrategyContext context)
    {
        return context.LifetimeEvent == PipelineLifetimeEvent.PipelineExecuting ? CommitAction.Commit : CommitAction.Default;
    }
}