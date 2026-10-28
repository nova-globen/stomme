using Stomme.Engine.Extensions;

namespace Stomme.Engine.Pipelines;

public class DefaultStepInputEvaluator : IStepInputEvaluator
{
    public async Task<object?> EvaluateAsync(StepInputEvaluatorContext context)
    {
        var wrappedInput = context.Input;
        var evaluator = context.ExpressionEvaluator;
        var expressionExecutionContext = context.ExpressionExecutionContext;
        return await evaluator.EvaluateAsync(wrappedInput, expressionExecutionContext).ConfigureAwait(false);
    }
}