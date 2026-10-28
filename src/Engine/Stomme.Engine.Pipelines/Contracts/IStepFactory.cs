using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines;

[Obsolete("Use the CreateStep method on the StepConstructorContext context itself")]
public interface IStepFactory
{
    [Obsolete("Use the CreateStep method on the StepConstructorContext context itself")]
    IStep Create(Type type, StepConstructorContext context);
}

[Obsolete("Use the CreateStep method on the StepConstructorContext context itself")]
public static class StepFactoryExtensions
{
    [Obsolete("Use the CreateStep method on the StepConstructorContext context itself")]
    public static IStep CreateStep<T>(this IStepFactory factory, StepConstructorContext context)
    {
        return factory.Create(typeof(T), context);
    }
}