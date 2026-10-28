namespace Stomme.Engine.Pipelines.CommitStates;

public class DefaultCommitStrategyRegistry : ICommitStrategyRegistry
{
    private readonly IDictionary<string, PipelineCommitStrategyRegistration> _pipelineStrategies = new Dictionary<string, PipelineCommitStrategyRegistration>();
    private readonly IDictionary<string, StepCommitStrategyRegistration> _stepStrategies = new Dictionary<string, StepCommitStrategyRegistration>();


    public IEnumerable<PipelineCommitStrategyRegistration> ListPipelineStrategyRegistrations()
    {
        return _pipelineStrategies.Values;
    }

    public IEnumerable<StepCommitStrategyRegistration> ListStepStrategyRegistrations()
    {
        return _stepStrategies.Values;
    }

    public void RegisterStrategy(PipelineCommitStrategyRegistration registration)
    {
        _pipelineStrategies[registration.Metadata.Name] = registration;
    }

    public void RegisterStrategy(StepCommitStrategyRegistration registration)
    {
        _stepStrategies[registration.Metadata.Name] = registration;
    }

    public IPipelineCommitStrategy? FindPipelineStrategy(string name)
    {
        return _pipelineStrategies.TryGetValue(name, out var registration) ? registration.Strategy : null;
    }

    public IStepCommitStrategy? FindStepStrategy(string name)
    {
        return _stepStrategies.TryGetValue(name, out var registration) ? registration.Strategy : null;
    }
}