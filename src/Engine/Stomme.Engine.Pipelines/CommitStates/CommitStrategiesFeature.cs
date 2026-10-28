using Stomme.Engine.Extensions;
using Stomme.Engine.Features.Abstractions;
using Stomme.Engine.Features.Services;
using Stomme.Engine.Pipelines.CommitStates.Strategies;
using Stomme.Engine.Pipelines.CommitStates.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Stomme.Engine.Pipelines.CommitStates;

public class CommitStrategiesFeature(IModule module) : FeatureBase(module)
{
    public void AddStandardStrategies()
    {
        // Pipeline commit strategies.
        Add(new PipelineExecutingPipelineStrategy());
        Add(new PipelineExecutedPipelineStrategy());
        Add(new StepExecutingPipelineStrategy());
        Add(new StepExecutedPipelineStrategy());

        // Step commit strategies.
        Add(new CommitAlwaysStepStrategy());
        Add(new CommitNeverStepStrategy());
        Add(new ExecutingStepStrategy());
        Add(new ExecutedStepStrategy());
    }

    public void Add(IPipelineCommitStrategy strategy)
    {
        var registration = ObjectRegistrationFactory.Describe(strategy);
        Add(registration);
    }

    public void Add(string displayName, IPipelineCommitStrategy strategy)
    {
        var registration = ObjectRegistrationFactory.Describe(strategy);
        registration.Metadata.DisplayName = displayName;
        Add(registration);
    }

    public void Add(string displayName, string description, IPipelineCommitStrategy strategy)
    {
        var registration = ObjectRegistrationFactory.Describe(strategy);
        registration.Metadata.DisplayName = displayName;
        registration.Metadata.Description = description;
        Add(registration);
    }

    public void Add(string name, string displayName, string description, IPipelineCommitStrategy strategy)
    {
        var registration = ObjectRegistrationFactory.Describe(strategy);
        registration.Metadata.Name = name;
        registration.Metadata.DisplayName = displayName;
        registration.Metadata.Description = description;
        Add(registration);
    }

    public void Add(PipelineCommitStrategyRegistration registration)
    {
        Services.Configure<CommitStateOptions>(options => options.PipelineCommitStrategies[registration.Metadata.Name] = registration);
    }

    public void Add(IStepCommitStrategy strategy)
    {
        var registration = ObjectRegistrationFactory.Describe(strategy);
        Add(registration);
    }

    public void Add(string displayName, IStepCommitStrategy strategy)
    {
        var registration = ObjectRegistrationFactory.Describe(strategy);
        registration.Metadata.DisplayName = displayName;
        Add(registration);
    }

    public void Add(string displayName, string description, IStepCommitStrategy strategy)
    {
        var registration = ObjectRegistrationFactory.Describe(strategy);
        registration.Metadata.DisplayName = displayName;
        registration.Metadata.Description = description;
        Add(registration);
    }

    public void Add(string name, string displayName, string description, IStepCommitStrategy strategy)
    {
        var registration = ObjectRegistrationFactory.Describe(strategy);
        registration.Metadata.Name = name;
        registration.Metadata.DisplayName = displayName;
        registration.Metadata.Description = description;
        Add(registration);
    }

    public void Add(StepCommitStrategyRegistration registration)
    {
        Services.Configure<CommitStateOptions>(options => options.StepCommitStrategies[registration.Metadata.Name] = registration);
    }

    /// <summary>
    /// Sets the specified pipeline commit strategy as the global default.
    /// The strategy will not be added to the registry and will only serve as a fallback when pipelines do not specify their own strategy.
    /// </summary>
    /// <param name="strategy">The pipeline commit strategy instance to use as the default.</param>
    public void SetDefaultPipelineCommitStrategy(IPipelineCommitStrategy strategy)
    {
        Services.Configure<CommitStateOptions>(options => options.DefaultPipelineCommitStrategy = strategy);
    }

    /// <summary>
    /// Sets the specified step commit strategy as the global default.
    /// The strategy will not be added to the registry and will only serve as a fallback when steps do not specify their own strategy.
    /// </summary>
    /// <param name="strategy">The step commit strategy instance to use as the default.</param>
    public void SetDefaultStepCommitStrategy(IStepCommitStrategy strategy)
    {
        Services.Configure<CommitStateOptions>(options => options.DefaultStepCommitStrategy = strategy);
    }

    public override void Apply()
    {
        Services.AddSingleton<ICommitStrategyRegistry, DefaultCommitStrategyRegistry>();
        Services.AddStartupTask<PopulateCommitStrategyRegistry>();
    }
}