using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Models;

namespace Stomme.Engine.Expressions;

/// <summary>
/// An expression handler for Delegate expressions.
/// </summary>
public class DelegateExpressionHandler : IExpressionHandler
{
    /// <inheritdoc />
    public async ValueTask<object?> EvaluateAsync(Expression expression, Type returnType, ExpressionExecutionContext context, ExpressionEvaluatorOptions options)
    {
        var value = expression.Value is Func<ExpressionExecutionContext, ValueTask<object?>> @delegate ? await @delegate(context).ConfigureAwait(false) : null;
        return value;
    }
}