namespace Stomme.Engine.Pipelines.CommitStates;

public interface ICommitStrategyRegistry
{
    IEnumerable<PipelineCommitStrategyRegistration> ListPipelineStrategyRegistrations();
    IEnumerable<StepCommitStrategyRegistration> ListStepStrategyRegistrations();
    void RegisterStrategy(PipelineCommitStrategyRegistration registration);
    void RegisterStrategy(StepCommitStrategyRegistration registration);
    IPipelineCommitStrategy? FindPipelineStrategy(string name);
    IStepCommitStrategy? FindStepStrategy(string name);
}