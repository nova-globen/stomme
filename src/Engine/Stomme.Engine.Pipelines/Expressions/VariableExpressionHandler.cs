using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines.Memory;

namespace Stomme.Engine.Pipelines.Expressions;

/// <summary>
/// Handles Variable expressions.
/// </summary>
public class VariableExpressionHandler : IExpressionHandler
{
    /// <inheritdoc />
    public ValueTask<object?> EvaluateAsync(Expression expression, Type returnType, ExpressionExecutionContext context, ExpressionEvaluatorOptions options)
    {
        var variable = expression.Value as Variable;
        var value = variable?.Get(context);
        return ValueTask.FromResult(value);
    }
}