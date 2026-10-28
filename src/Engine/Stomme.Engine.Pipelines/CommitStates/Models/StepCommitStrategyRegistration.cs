namespace Stomme.Engine.Pipelines.CommitStates;

public class StepCommitStrategyRegistration : ObjectRegistration<IStepCommitStrategy, CommitStrategyMetadata>
{
    public StepCommitStrategyRegistration()
    {
    }
    
    public StepCommitStrategyRegistration(IStepCommitStrategy strategy, CommitStrategyMetadata metadata)
    {
        Strategy = strategy;
        Metadata = metadata;
    }
}