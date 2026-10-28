namespace Stomme.Engine.Pipelines.CommitStates;

public record StepStrategyDescriptor(string Name, string Description, IStepCommitStrategy Strategy);