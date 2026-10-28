namespace Stomme.Engine.Pipelines;

public interface IStepInputEvaluator
{
    Task<object?> EvaluateAsync(StepInputEvaluatorContext context);
}