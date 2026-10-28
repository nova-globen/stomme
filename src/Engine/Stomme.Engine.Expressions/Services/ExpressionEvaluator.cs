using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Models;

namespace Stomme.Engine.Expressions.Services;

/// <inheritdoc />
public class ExpressionEvaluator(IExpressionDescriptorRegistry registry, IServiceProvider serviceProvider) : IExpressionEvaluator
{
    /// <inheritdoc />
    public async ValueTask<T?> EvaluateAsync<T>(Expression expression, ExpressionExecutionContext context, ExpressionEvaluatorOptions? options = null)
    {
        return (T?)await EvaluateAsync(expression, typeof(T), context, options).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<object?> EvaluateAsync(Expression expression, Type returnType, ExpressionExecutionContext context, ExpressionEvaluatorOptions? options = null)
    {
        var expressionType = expression.Type;
        var expressionDescriptor = registry.Find(expressionType);

        if (expressionDescriptor == null)
            throw new($"Could not find a descriptor for expression type \"{expressionType}\".");

        var handler = expressionDescriptor.HandlerFactory(serviceProvider);
        options ??= new();
        return await handler.EvaluateAsync(expression, returnType, context, options).ConfigureAwait(false);
    }
}