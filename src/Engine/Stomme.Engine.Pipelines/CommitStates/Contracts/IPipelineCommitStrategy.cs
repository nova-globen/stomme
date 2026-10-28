namespace Stomme.Engine.Pipelines.CommitStates;

public interface IPipelineCommitStrategy
{
    CommitAction ShouldCommit(PipelineCommitStateStrategyContext context);
}