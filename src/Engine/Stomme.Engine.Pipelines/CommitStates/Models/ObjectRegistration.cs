namespace Stomme.Engine.Pipelines.CommitStates;

public class ObjectRegistration<T, TMeta>
{
    public T Strategy { get; set; } = default!;
    public TMeta Metadata { get; set; } = default!;
}