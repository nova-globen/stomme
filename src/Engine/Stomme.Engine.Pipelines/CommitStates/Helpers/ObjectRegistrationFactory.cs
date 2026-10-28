namespace Stomme.Engine.Pipelines.CommitStates;

public static class ObjectRegistrationFactory
{
    public static PipelineCommitStrategyRegistration Describe(IPipelineCommitStrategy strategy)
    {
        var metadata = ObjectMetadataDescriber.Describe(strategy.GetType());
        return new(strategy, metadata);
    }
    
    public static StepCommitStrategyRegistration Describe(IStepCommitStrategy strategy)
    {
        var metadata = ObjectMetadataDescriber.Describe(strategy.GetType());
        return new(strategy, metadata);
    }
}