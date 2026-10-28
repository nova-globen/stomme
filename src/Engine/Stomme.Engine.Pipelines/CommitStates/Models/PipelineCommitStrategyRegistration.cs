namespace Stomme.Engine.Pipelines.CommitStates;

public class PipelineCommitStrategyRegistration : ObjectRegistration<IPipelineCommitStrategy, CommitStrategyMetadata>
{
    public PipelineCommitStrategyRegistration()
    {
    }

    public PipelineCommitStrategyRegistration(IPipelineCommitStrategy strategy, CommitStrategyMetadata metadata)
    {
        Strategy = strategy;
        Metadata = metadata;
    }
}