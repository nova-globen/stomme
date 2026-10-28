namespace Stomme.Engine.Pipelines.Models;

public class StepConstructionResult(IStep step, IEnumerable<Exception>? exceptions = null)
{
    public bool HasExceptions => Exceptions.Any();

    public IStep Step { get; } = step;
    public IEnumerable<Exception> Exceptions { get; } = exceptions ?? [];

    public StepConstructionResult<TStep> Cast<TStep>() where TStep : IStep => new ((TStep)Step, Exceptions);
}

public sealed class StepConstructionResult<TStep>(TStep step, IEnumerable<Exception>? exceptions = null) : StepConstructionResult(step, exceptions)
    where TStep : IStep
{ 
    public new TStep Step { get; } = step;
}
