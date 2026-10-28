using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Extensions;
using Stomme.Engine.Pipelines.Models;

namespace Stomme.Engine.Pipelines.Expressions;

/// <summary>
/// Evaluates an <see cref="Output"/> expression.
/// </summary>
public class OutputExpressionHandler : IExpressionHandler
{
    /// <inheritdoc />
    public ValueTask<object?> EvaluateAsync(Expression expression, Type returnType, ExpressionExecutionContext context, ExpressionEvaluatorOptions options)
    {
        var value = expression.Value is Output output ? context.Get(output) : default;
        return ValueTask.FromResult(value);
    }
}