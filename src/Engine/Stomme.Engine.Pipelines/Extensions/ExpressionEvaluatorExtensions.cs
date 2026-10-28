using Stomme.Engine.Expressions.Contracts;
using Stomme.Engine.Expressions.Models;
using Stomme.Engine.Pipelines.Models;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Contains extension methods for <see cref="IExpressionEvaluator"/>.
/// </summary>
public static class ExpressionEvaluatorExtensions
{
    extension(IExpressionEvaluator evaluator)
    {
        /// <summary>
        /// Evaluates the specified expression and returns the result.
        /// </summary>
        public ValueTask<T?> EvaluateAsync<T>(Input<T> input, ExpressionExecutionContext context, ExpressionEvaluatorOptions? options = default)
        {
            return evaluator.EvaluateAsync<T>(input.Expression!, context, options);
        }

        /// <summary>
        /// Evaluates the specified expression and returns the result.
        /// </summary>
        public ValueTask<object?> EvaluateAsync(Input input, ExpressionExecutionContext context, ExpressionEvaluatorOptions? options = default)
        {
            return evaluator.EvaluateAsync(input.Expression!, input.Type, context, options);
        }
    }
}