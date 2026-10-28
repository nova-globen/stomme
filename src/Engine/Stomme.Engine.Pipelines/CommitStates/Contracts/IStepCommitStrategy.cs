namespace Stomme.Engine.Pipelines.CommitStates;

public interface IStepCommitStrategy
{
    CommitAction ShouldCommit(StepCommitStateStrategyContext context);
}