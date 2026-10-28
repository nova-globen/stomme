using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

/// <inheritdoc />
public class StepFactory : IStepFactory
{
    /// <inheritdoc />
    public IStep Create(Type type, StepConstructorContext context)
    {
        return context.CreateStep(type).Step;
    }
}